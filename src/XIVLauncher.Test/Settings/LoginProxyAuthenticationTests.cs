using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XIVLauncher.Common.Http;
using Xunit;

namespace XIVLauncher.Test.Settings;

[Collection("WPF UI")]
public sealed class LoginProxyAuthenticationTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task RotationUsesEachProxysOwnCredentials(string scheme)
    {
        await using var first = new LoopbackProxy("first", "first-password");
        await using var second = new LoopbackProxy("second", "second-password");
        var pool = new LoginProxyPool();
        pool.Configure(true, [first.Entry, second.Entry], LoginProxyPickMode.Sticky, null, null, null);
        using var client = CreateClient(pool, first, second);

        await AssertSuccess(client, $"{scheme}://login.example.com/check");
        Assert.True(pool.TryRotateCurrent());
        await AssertSuccess(client, $"{scheme}://login.example.com/check");
        Assert.True(pool.TryRotateCurrent());
        await AssertSuccess(client, $"{scheme}://login.example.com/check");

        Assert.NotEmpty(first.ReceivedCredentials);
        Assert.NotEmpty(second.ReceivedCredentials);
        Assert.All(first.ReceivedCredentials, value => Assert.Equal(first.Authorization, value));
        Assert.All(second.ReceivedCredentials, value => Assert.Equal(second.Authorization, value));
    }

    [Fact]
    public async Task EnablingProxyAfterFirstRequestUsesNewCredentials()
    {
        var originalProxy = HttpClient.DefaultProxy;
        await using var direct = new LoopbackProxy();
        await using var proxy = new LoopbackProxy("enabled", "test-password");
        try
        {
            HttpClient.DefaultProxy = new WebProxy();
            var pool = new LoginProxyPool();
            pool.Configure(false, [proxy.Entry], LoginProxyPickMode.Sticky, null, null, null);
            using var client = CreateClient(pool, proxy);
            await AssertSuccess(client, direct.Address.AbsoluteUri);

            pool.Configure(true, [proxy.Entry], LoginProxyPickMode.Sticky, null, null, null);
            await AssertSuccess(client, "https://login.example.com/check");
            Assert.Contains(proxy.Authorization!, proxy.ReceivedCredentials);
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
        }
    }

    [Fact]
    public async Task DisablingPoolUsesSystemProxysCredentials()
    {
        var originalProxy = HttpClient.DefaultProxy;
        await using var loginProxy = new LoopbackProxy("login", "login-password");
        await using var systemProxy = new LoopbackProxy("system", "system-password");
        try
        {
            Assert.True(systemProxy.Entry.TryGetProxyParts(out var systemUri, out var credentials));
            HttpClient.DefaultProxy = new WebProxy(systemUri) { Credentials = credentials };
            var pool = new LoginProxyPool();
            pool.Configure(true, [loginProxy.Entry], LoginProxyPickMode.Sticky, null, null, null);
            using var client = CreateClient(pool, loginProxy, systemProxy);
            await AssertSuccess(client, "https://login.example.com/check");

            pool.Configure(false, [loginProxy.Entry], LoginProxyPickMode.Sticky, null, null, null);
            await AssertSuccess(client, "https://login.example.com/check");
            Assert.NotEmpty(systemProxy.ReceivedCredentials);
            Assert.All(systemProxy.ReceivedCredentials, value => Assert.Equal(systemProxy.Authorization, value));
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
        }
    }

    [Fact]
    public void CachedCredentialResolverKeepsInFlightRequestsBoundToTheirProxy()
    {
        var first = new LoginProxyEntry { Id = "first", Url = "http://first:one@first.example.com:8080" };
        var second = new LoginProxyEntry { Id = "second", Url = "http://second:two@second.example.com:8080" };
        var pool = new LoginProxyPool();
        pool.Configure(true, [first, second], LoginProxyPickMode.Sticky, null, null, null);
        var cachedCredentials = pool.WebProxy.Credentials!;
        var destination = new Uri("https://login.example.com");
        var firstUri = pool.WebProxy.GetProxy(destination)!;
        pool.TryRotateCurrent();
        var secondUri = pool.WebProxy.GetProxy(destination)!;

        Assert.Same(cachedCredentials, pool.WebProxy.Credentials);
        Assert.Equal("first", cachedCredentials.GetCredential(firstUri, "Basic")?.UserName);
        Assert.Equal("second", cachedCredentials.GetCredential(secondUri, "Basic")?.UserName);
        Assert.Null(cachedCredentials.GetCredential(new Uri("http://unknown.example.com"), "Basic"));

        second.Url = "http://second:updated@second.example.com:8080";
        pool.WebProxy.GetProxy(destination);
        Assert.Equal("updated", cachedCredentials.GetCredential(secondUri, "Basic")?.Password);
    }

    private static HttpClient CreateClient(LoginProxyPool pool, params LoopbackProxy[] proxies) =>
        new(new SocketsHttpHandler
        {
            Proxy = pool.WebProxy,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    proxies.Any(proxy => proxy.Certificate.GetCertHashString() == certificate?.GetCertHashString())
            }
        }) { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task AssertSuccess(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class LoopbackProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task server;

        public X509Certificate2 Certificate { get; }
        public Uri Address { get; }
        public LoginProxyEntry Entry { get; }
        public string? Authorization { get; }
        public ConcurrentQueue<string> ReceivedCredentials { get; } = new();

        public LoopbackProxy(string? user = null, string? password = null)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=login.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Windows SChannel needs a re-imported key for the loopback TLS server.
            Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
            listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            Entry = new LoginProxyEntry { Id = user ?? "direct", Url = new UriBuilder(Address) { UserName = user ?? "", Password = password ?? "" }.Uri.AbsoluteUri };
            Authorization = user == null ? null : "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
            server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream = connection.GetStream();
                    var (connect, authorization) = await ReadRequestAsync(stream);
                    if (authorization != null)
                        ReceivedCredentials.Enqueue(authorization);

                    if (Authorization != null && authorization != Authorization)
                    {
                        await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=tests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        continue;
                    }

                    const string success = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK";
                    if (connect)
                    {
                        await WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, lifetime.Token);
                        await ReadRequestAsync(tls);
                        await WriteAsync(tls, success);
                    }
                    else
                    {
                        await WriteAsync(stream, success);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }

        private async Task<(bool Connect, string? Authorization)> ReadRequestAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(lifetime.Token);
            string? authorization = null;
            while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } line)
                if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                    authorization = line.Split(':', 2)[1].Trim();

            return (requestLine?.StartsWith("CONNECT ", StringComparison.Ordinal) == true, authorization);
        }

        private async Task WriteAsync(Stream stream, string response) =>
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), lifetime.Token);

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            try { await server; }
            finally { Certificate.Dispose(); lifetime.Dispose(); }
        }
    }
}
