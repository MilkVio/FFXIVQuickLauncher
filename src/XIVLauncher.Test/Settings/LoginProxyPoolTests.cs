using System.IO;
using XIVLauncher.Common.Http;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class LoginProxyPoolTests
{
    private static readonly Uri Destination = new("https://cas.example.com/authen/test.json");

    private static LoginProxyEntry Entry(string id, bool enabled = true, string url = "http://user:pass@127.0.0.1:18081") =>
        new() { Id = id, Name = id, Url = url, Enabled = enabled };

    private static LoginProxyPool CreatePool
    (
        bool                           enabled,
        LoginProxyPickMode             mode          = LoginProxyPickMode.Sticky,
        string?                        manualId      = null,
        string?                        lastWorkingId = null,
        Action<string?>?               persist       = null,
        params LoginProxyEntry[]       entries
    )
    {
        var pool = new LoginProxyPool();
        pool.Configure(enabled, entries, mode, manualId, lastWorkingId, persist);
        return pool;
    }

    [Fact]
    public void InactivePoolDoesNotReturnEntryProxy()
    {
        var entry = Entry("a");
        entry.TryGetProxyParts(out var entryProxyUri, out _);

        var disabledPool = CreatePool(false, entries: entry);
        Assert.NotEqual(entryProxyUri, disabledPool.WebProxy.GetProxy(Destination));

        var noEntryPool = CreatePool(true);
        Assert.NotEqual(entryProxyUri, noEntryPool.WebProxy.GetProxy(Destination));
        Assert.False(disabledPool.IsActive);
        Assert.False(noEntryPool.IsActive);
    }

    [Fact]
    public void ActivePoolReturnsCurrentEntryProxy()
    {
        var pool = CreatePool(true, entries: Entry("a"));
        Assert.True(pool.IsActive);
        Assert.Equal("a", pool.CurrentEntryId);

        var proxyUri = pool.WebProxy.GetProxy(Destination);
        Assert.Equal("127.0.0.1", proxyUri?.Host);
        Assert.Equal(18081, proxyUri?.Port);
        Assert.False(pool.WebProxy.IsBypassed(Destination));
    }

    [Fact]
    public void StickyPrefersLastWorkingEntry()
    {
        var pool = CreatePool(true, lastWorkingId: "b", entries: [Entry("a"), Entry("b")]);
        Assert.Equal("b", pool.CurrentEntryId);
    }

    [Fact]
    public void StickyFallsBackToFirstEnabledEntry()
    {
        var pool = CreatePool(true, lastWorkingId: "missing", entries: [Entry("a"), Entry("b", enabled: false), Entry("c")]);
        Assert.Equal("a", pool.CurrentEntryId);
    }

    [Fact]
    public void RotationCyclesThroughEnabledEntries()
    {
        var pool = CreatePool(true, entries: [Entry("a"), Entry("b", enabled: false), Entry("c")]);

        Assert.True(pool.TryRotateCurrent());
        Assert.Equal("c", pool.CurrentEntryId);

        Assert.True(pool.TryRotateCurrent());
        Assert.Equal("a", pool.CurrentEntryId);
    }

    [Fact]
    public void RotationFailsWithoutAlternative()
    {
        var pool = CreatePool(true, entries: Entry("a"));
        Assert.False(pool.TryRotateCurrent());
        Assert.Equal("a", pool.CurrentEntryId);

        var inactive = CreatePool(false, entries: [Entry("a"), Entry("b")]);
        Assert.False(inactive.TryRotateCurrent());
    }

    [Fact]
    public void ManualRotationKeepsPersistUntouched()
    {
        var persisted = new List<string?>();
        var pool = CreatePool(true, LoginProxyPickMode.Manual, manualId: "b", persist: persisted.Add, entries: [Entry("a"), Entry("b")]);

        Assert.Equal("b", pool.CurrentEntryId);

        Assert.True(pool.TryRotateCurrent());
        Assert.Equal("a", pool.CurrentEntryId);

        pool.ReportCurrentSuccess();
        Assert.Empty(persisted);
    }

    [Fact]
    public void StickySuccessPersistsOnlyOnChange()
    {
        var persisted = new List<string?>();
        var pool = CreatePool(true, persist: persisted.Add, entries: [Entry("a"), Entry("b")]);

        pool.ReportCurrentSuccess();
        pool.ReportCurrentSuccess();
        Assert.Equal(["a"], persisted);

        pool.TryRotateCurrent();
        pool.ReportCurrentSuccess();
        Assert.Equal(["a", "b"], persisted);
    }

    [Fact]
    public void RandomPicksWithinEnabledEntries()
    {
        var entries  = new[] { Entry("a"), Entry("b", enabled: false), Entry("c") };
        var pool     = CreatePool(true, LoginProxyPickMode.Random, entries: entries);

        for (var i = 0; i < 20; i++)
        {
            pool.Configure(true, entries, LoginProxyPickMode.Random, null, null, null);
            Assert.Contains(pool.CurrentEntryId, new[] { "a", "c" });
        }
    }

    [Fact]
    public void ProxyPartsParseCredentials()
    {
        var entry = Entry("a", url: "http://user%40corp:p%40ss@proxy.example.com:8080");
        Assert.True(entry.TryGetProxyParts(out var proxyUri, out var credentials));

        Assert.Equal("proxy.example.com", proxyUri.Host);
        Assert.Equal(8080, proxyUri.Port);

        var networkCredential = credentials?.GetCredential(proxyUri, "Basic");
        Assert.Equal("user@corp", networkCredential?.UserName);
        Assert.Equal("p@ss", networkCredential?.Password);
    }

    [Theory]
    [InlineData("https://proxy.example.com:8080")]
    [InlineData("not a url")]
    [InlineData("http://")]
    public void ProxyPartsRejectInvalidUrls(string url)
    {
        Assert.False(Entry("a", url: url).TryGetProxyParts(out _, out _));
    }

    [Fact]
    public void MaskedUrlHidesPasswordOnly()
    {
        Assert.Equal("http://user:***@proxy.example.com:8080", Entry("a", url: "http://user:secret@proxy.example.com:8080").GetMaskedUrl());
        Assert.Equal("http://proxy.example.com:8080", Entry("a", url: "http://proxy.example.com:8080").GetMaskedUrl());
        Assert.Equal("not a url", Entry("a", url: "not a url").GetMaskedUrl());
    }

    [Fact]
    public void SettingsRoundTripKeepsProxyConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VioletSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path     = Path.Combine(directory, "config.json");
            var settings = LauncherSettingsV3.Load(path);

            settings.LoginProxyEnabled            = true;
            settings.LoginProxyPickMode           = LoginProxyPickMode.Manual;
            settings.LoginProxyManualEntryId      = "b";
            settings.LoginProxyLastWorkingEntryId = "a";
            settings.LoginProxyEntries            = [Entry("a"), Entry("b", enabled: false)];

            var loaded = LauncherSettingsV3.Load(path);
            Assert.True(loaded.LoginProxyEnabled);
            Assert.Equal(LoginProxyPickMode.Manual, loaded.LoginProxyPickMode);
            Assert.Equal("b", loaded.LoginProxyManualEntryId);
            Assert.Equal("a", loaded.LoginProxyLastWorkingEntryId);
            Assert.Equal(2, loaded.LoginProxyEntries.Count);
            Assert.Equal("a", loaded.LoginProxyEntries[0].Id);
            Assert.False(loaded.LoginProxyEntries[1].Enabled);
            Assert.Equal("http://user:pass@127.0.0.1:18081", loaded.LoginProxyEntries[0].Url);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
