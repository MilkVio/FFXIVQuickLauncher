using System.IO;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using XIVLauncher.Update;
using Xunit;

namespace XIVLauncher.Test.Update;

public sealed class VioletUpdateSourceTests
{
    [Theory]
    [InlineData(false, "2.4.9")]
    [InlineData(true, "2.5.0")]
    public async Task UpdatesUseVioletReleasesAndRespectPrereleaseSetting(bool prerelease, string version)
    {
        var downloader = new ReleaseDownloader();
        var source = UpdateOrchestrator.CreateUpdateSource(prerelease, downloader);
        var local = new VelopackAsset
        {
            PackageId = "XIVLauncherCN",
            Version = SemanticVersion.Parse("2.3.9"),
            Type = VelopackAssetType.Full,
            FileName = "XIVLauncherCN-2.3.9-full.nupkg"
        };
        var directory = Path.Combine(Path.GetTempPath(), "VioletUpdateTests", Guid.NewGuid().ToString("N"));
        var locator = new TestVelopackLocator("XIVLauncherCN", "2.3.9", directory, directory, directory,
            Path.Combine(directory, "Update.exe"), "win", localPackage: local);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false }, locator);

        var update = await manager.CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal(version, update.TargetFullRelease.Version.ToString());
        Assert.Equal(VelopackAssetType.Full, update.TargetFullRelease.Type);
        Assert.NotEmpty(update.DeltasToTarget);
        Assert.All(update.DeltasToTarget, asset => Assert.Equal(VelopackAssetType.Delta, asset.Type));
        Assert.Equal("https://api.github.com/repos/MilkVio/FFXIVQuickLauncher/releases?per_page=5&page=1", downloader.ApiUrl);
        Assert.All(downloader.FeedUrls, url => Assert.StartsWith("https://github.com/MilkVio/FFXIVQuickLauncher/releases/download/", url));
        Assert.Equal(prerelease, downloader.FeedUrls.Any(url => url.Contains("2.5.0", StringComparison.Ordinal)));
    }

    private sealed class ReleaseDownloader : IFileDownloader
    {
        public string? ApiUrl { get; private set; }
        public List<string> FeedUrls { get; } = [];

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            ApiUrl = url;
            Assert.Equal("XIVLauncherCN-Violet", headers!["User-Agent"]);
            return Task.FromResult(JsonSerializer.Serialize(new[]
            {
                Release("2.5.0", prerelease: true),
                Release("2.4.9"),
                Release("2.3.9")
            }));
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            FeedUrls.Add(url);
            var version = url.Split('/')[^2];
            var json = JsonSerializer.Serialize(new
            {
                Assets = new[] { Asset(version, "Full", 1000000), Asset(version, "Delta", 1000) }
            });
            // Real release indexes may carry a UTF-8 BOM.
            return Task.FromResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new InvalidOperationException("Update checks must not download or install packages");

        private static object Release(string version, bool prerelease = false)
        {
            var baseUrl = $"https://github.com/MilkVio/FFXIVQuickLauncher/releases/download/{version}";
            return new
            {
                name = version,
                prerelease,
                assets = new[]
                {
                    new { name = "releases.win.json", browser_download_url = $"{baseUrl}/releases.win.json" },
                    new { name = $"XIVLauncherCN-{version}-full.nupkg", browser_download_url = $"{baseUrl}/XIVLauncherCN-{version}-full.nupkg" },
                    new { name = $"XIVLauncherCN-{version}-delta.nupkg", browser_download_url = $"{baseUrl}/XIVLauncherCN-{version}-delta.nupkg" }
                }
            };
        }

        private static object Asset(string version, string type, long size) => new
        {
            PackageId = "XIVLauncherCN",
            Version = version,
            Type = type,
            FileName = $"XIVLauncherCN-{version}-{type.ToLowerInvariant()}.nupkg",
            SHA1 = new string('0', 40),
            SHA256 = new string('0', 64),
            Size = size
        };
    }
}
