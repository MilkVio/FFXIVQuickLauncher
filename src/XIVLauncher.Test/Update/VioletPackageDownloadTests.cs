using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using XIVLauncher.Support;
using Xunit;

namespace XIVLauncher.Test.Update;

public sealed class VioletPackageDownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "VioletPackages", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdapterDownloadsCompletePackageWithOrWithoutRangeSupport(bool ranges)
    {
        var payload = new byte[(5 << 20) + 19];
        Random.Shared.NextBytes(payload);
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("Violet-test", request.Headers.GetValues("X-Test").Single());
            var range = request.Headers.Range?.Ranges.Single();
            var start = ranges ? range!.From!.Value : 0;
            var end = ranges ? Math.Min(range!.To!.Value, payload.Length - 1) : payload.Length - 1;
            var response = new HttpResponseMessage(ranges ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload[(int)start..((int)end + 1)])
            };
            if (ranges) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
            return Task.FromResult(response);
        }));
        var target = Path.Combine(directory, "package.nupkg");
        var progress = new List<int>();
        await new XLHttpClientFileDownloader(client).DownloadFile("https://updates.test/package", target, progress.Add,
            new Dictionary<string, string> { ["X-Test"] = "Violet-test" });
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(100, progress[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledPackageIsRemoved(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, token) =>
        {
            if (cancel)
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var target = Path.Combine(directory, "partial.nupkg");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(target, "previous partial data");
        await Assert.ThrowsAnyAsync<Exception>(() => new XLHttpClientFileDownloader(client)
            .DownloadFile("https://updates.test/package", target, _ => { }, cancelToken: cts.Token));
        Assert.False(File.Exists(target));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
