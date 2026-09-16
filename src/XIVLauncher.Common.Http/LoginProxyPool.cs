using System.Diagnostics;
using System.Net;
using Serilog;

namespace XIVLauncher.Common.Http;

/// <summary>
///     登陆代理池: 持有条目配置与当前入口, 通过动态 <see cref="IWebProxy"/> 供各 HttpClient 使用。
///     池未激活时代理包装透明回退系统代理, 行为与不注入时一致。
/// </summary>
public sealed class LoginProxyPool
{
    public static LoginProxyPool Shared { get; } = new();

    private readonly object syncRoot = new();
    private readonly PoolWebProxy webProxy;

    private IReadOnlyList<LoginProxyEntry> entries = [];
    private bool                         proxyEnabled;
    private LoginProxyPickMode           pickMode;
    private string?                      manualEntryId;
    private string?                      lastWorkingEntryId;
    private Action<string?>?             persistLastWorking;
    private string?                      currentEntryId;

    public LoginProxyPool() =>
        webProxy = new PoolWebProxy(this);

    /// <summary>
    ///     供 HttpClient 注入的动态代理; 每次请求按当前入口解析
    /// </summary>
    public IWebProxy WebProxy => webProxy;

    public bool IsActive
    {
        get
        {
            lock (syncRoot)
                return proxyEnabled && GetCurrentEntryCore() != null;
        }
    }

    public string? CurrentEntryId
    {
        get
        {
            lock (syncRoot)
                return currentEntryId;
        }
    }

    public void Configure
    (
        bool                           enabled,
        IReadOnlyList<LoginProxyEntry> proxyEntries,
        LoginProxyPickMode             mode,
        string?                        manualId,
        string?                        lastWorkingId,
        Action<string?>?               persistLastWorkingCallback
    )
    {
        lock (syncRoot)
        {
            proxyEnabled       = enabled;
            entries            = proxyEntries?.ToList() ?? [];
            pickMode           = mode;
            manualEntryId      = manualId;
            lastWorkingEntryId = lastWorkingId;
            persistLastWorking = persistLastWorkingCallback;
            currentEntryId     = PickInitialEntryId();
        }
    }

    /// <summary>
    ///     传输层失败后把当前入口切到下一个启用条目; 无其他可用条目时返回 false。
    ///     手动模式下同样允许切换, 仅改运行态, 不改指定配置。
    /// </summary>
    public bool TryRotateCurrent()
    {
        lock (syncRoot)
        {
            var enabledEntries = GetEnabledEntries();
            if (!proxyEnabled || enabledEntries.Count < 2)
                return false;

            LoginProxyEntry next;
            if (pickMode == LoginProxyPickMode.Random)
            {
                var candidates = enabledEntries.Where(e => e.Id != currentEntryId).ToList();
                next = candidates[Random.Shared.Next(candidates.Count)];
            }
            else
            {
                var index = enabledEntries.FindIndex(e => e.Id == currentEntryId);
                next = enabledEntries[(index + 1 + enabledEntries.Count) % enabledEntries.Count];
            }

            currentEntryId = next.Id;
            Log.Warning("[LoginProxyPool] 切换代理入口: {EntryName}", next.Name);
            return true;
        }
    }

    /// <summary>
    ///     请求成功后调用; 粘滞模式下把当前入口记录为上次可用入口
    /// </summary>
    public void ReportCurrentSuccess()
    {
        Action<string?>? persist = null;
        string?          id      = null;

        lock (syncRoot)
        {
            if (!proxyEnabled || currentEntryId == null)
                return;

            if (pickMode == LoginProxyPickMode.Sticky && lastWorkingEntryId != currentEntryId)
            {
                lastWorkingEntryId = currentEntryId;
                persist            = persistLastWorking;
                id                 = currentEntryId;
            }
        }

        persist?.Invoke(id);
    }

    /// <summary>
    ///     经指定条目请求轻量地址验证可用性, 供设置界面手动测速
    /// </summary>
    public static async Task<ProxyTestResult> TestEntryAsync(LoginProxyEntry entry, string testUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.TryGetProxyParts(out var proxyUri, out var credentials))
            return ProxyTestResult.Failure("地址格式无效");

        var handler = new SocketsHttpHandler
        {
            UseProxy       = true,
            Proxy          = new WebProxy(proxyUri) { Credentials = credentials },
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var       stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return response.IsSuccessStatusCode
                       ? ProxyTestResult.Success(stopwatch.ElapsedMilliseconds)
                       : ProxyTestResult.Failure($"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProxyTestResult.Failure("连接超时");
        }
        catch (Exception ex)
        {
            return ProxyTestResult.Failure(ex.Message);
        }
    }

    private List<LoginProxyEntry> GetEnabledEntries() =>
        entries.Where(e => e.Enabled).ToList();

    private LoginProxyEntry? GetCurrentEntryCore()
    {
        if (!proxyEnabled || currentEntryId == null)
            return null;

        return entries.FirstOrDefault(e => e.Enabled && e.Id == currentEntryId);
    }

    private string? PickInitialEntryId()
    {
        var enabledEntries = GetEnabledEntries();
        if (!proxyEnabled || enabledEntries.Count == 0)
            return null;

        return pickMode switch
        {
            LoginProxyPickMode.Manual => enabledEntries.FirstOrDefault(e => e.Id == manualEntryId)?.Id      ?? enabledEntries[0].Id,
            LoginProxyPickMode.Random => enabledEntries[Random.Shared.Next(enabledEntries.Count)].Id,
            _                         => enabledEntries.FirstOrDefault(e => e.Id == lastWorkingEntryId)?.Id ?? enabledEntries[0].Id
        };
    }

    private LoginProxyEntry? GetCurrentEntry()
    {
        lock (syncRoot)
            return GetCurrentEntryCore();
    }

    private sealed class PoolWebProxy(LoginProxyPool pool) : IWebProxy, ICredentials
    {
        private readonly Dictionary<Uri, ICredentials?> credentialsByProxy = [];

        // HttpClient caches this object; credentials must follow the request's proxy URI.
        public ICredentials? Credentials
        {
            get => this;
            set { }
        }

        public Uri? GetProxy(Uri destination)
        {
            var entry = pool.GetCurrentEntry();
            Uri? proxyUri;
            ICredentials? credentials;
            if (entry != null && entry.TryGetProxyParts(out var entryUri, out credentials))
            {
                proxyUri = entryUri;
            }
            else
            {
                var defaultProxy = HttpClient.DefaultProxy;
                proxyUri    = defaultProxy.GetProxy(destination);
                credentials = defaultProxy.Credentials;
            }

            if (proxyUri != null)
            {
                lock (credentialsByProxy)
                    credentialsByProxy[proxyUri] = credentials;
            }

            return proxyUri;
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            ICredentials? credentials;
            lock (credentialsByProxy)
                credentialsByProxy.TryGetValue(uri, out credentials);

            return credentials?.GetCredential(uri, authType);
        }

        public bool IsBypassed(Uri host) =>
            pool.GetCurrentEntry() == null && HttpClient.DefaultProxy.IsBypassed(host);
    }
}

public readonly record struct ProxyTestResult(bool Ok, long LatencyMs, string? Error)
{
    public static ProxyTestResult Success(long latencyMs) => new(true, latencyMs, null);

    public static ProxyTestResult Failure(string error) => new(false, 0, error);
}
