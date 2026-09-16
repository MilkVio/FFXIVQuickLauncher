using XIVLauncher.Common.Http;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Windows.ViewModel;
using Xunit;

namespace XIVLauncher.Test.Settings;

[Collection("WPF UI")]
public sealed class ProxyManagerWindowViewModelTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("edit")]
    public async Task TestAllCompletesItsSnapshotWhenEntriesChange(string change)
    {
        var contextProperty = typeof(App).GetProperty(nameof(App.StartupContext))!;
        var previousContext = contextProperty.GetValue(null);
        var settings = new LauncherSettingsV3
        {
            LoginProxyEntries = [new() { Id = "first" }, new() { Id = "second" }]
        };
        var firstTest = new TaskCompletionSource<ProxyTestResult>();
        var tested = new List<string>();

        try
        {
            contextProperty.SetValue(null, new StartupContext { Settings = settings });
            var model = new ProxyManagerWindowViewModel(testEntryAsync: entry =>
            {
                tested.Add(entry.Id);
                return entry.Id == "first" ? firstTest.Task : Task.FromResult(ProxyTestResult.Success(2));
            });
            var originalRows = model.Entries.ToArray();
            var run = model.TestAllCommand.ExecuteAsync(null);
            Assert.Equal(["first"], tested);

            settings.LoginProxyEntries = change switch
            {
                "add" => [..settings.LoginProxyEntries, new LoginProxyEntry { Id = "added" }],
                "remove" => [settings.LoginProxyEntries[0]],
                _ => [settings.LoginProxyEntries[0], new LoginProxyEntry { Id = "edited" }]
            };
            model.ReloadFromSettings();
            firstTest.SetResult(ProxyTestResult.Success(1));
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["first", "second"], tested);
            Assert.All(originalRows, row => Assert.False(row.IsTesting));
            Assert.False(model.TestAllCommand.IsRunning);

            tested.Clear();
            await model.TestAllCommand.ExecuteAsync(null);
            Assert.Equal(settings.LoginProxyEntries.Select(entry => entry.Id), tested);
        }
        finally
        {
            firstTest.TrySetResult(ProxyTestResult.Failure("Test ended"));
            contextProperty.SetValue(null, previousContext);
        }
    }
}
