using Velopack;

namespace XIVLauncher.Update;

internal interface ILauncherUpdateSession
{
    Task<UpdateInfo?> CheckAsync();
    Task DownloadAsync(UpdateInfo update, Action<int> progress);
    void ApplyAndRestart(UpdateInfo update);
}

internal sealed class LauncherUpdateSession(UpdateManager manager) : ILauncherUpdateSession
{
    public Task<UpdateInfo?> CheckAsync() => manager.CheckForUpdatesAsync();
    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => manager.DownloadUpdatesAsync(update, progress);
    public void ApplyAndRestart(UpdateInfo update) => manager.ApplyUpdatesAndRestart(update);
}
