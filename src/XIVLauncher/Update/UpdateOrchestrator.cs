using Serilog;
using Velopack;
using Velopack.Sources;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Windows;

namespace XIVLauncher.Update;

internal class UpdateOrchestrator(Func<bool, ILauncherUpdateSession>? createSession = null)
{
    public async Task<UpdateStartupResult> Run(
        bool downloadPrerelease,
        LoadingDialog? loadingDialog,
        ChangelogWindow? changelogWindow,
        Action? beforeShowChangelog = null)
    {
        var stage = LauncherUpdateStage.Checking;
        try
        {
            var session = createSession?.Invoke(downloadPrerelease) ?? new LauncherUpdateSession(new UpdateManager(
                CreateUpdateSource(downloadPrerelease), new UpdateOptions
                {
                    ExplicitChannel = "win",
                    AllowVersionDowngrade = false
                }));
            loadingDialog?.SetMessage("正在检查启动器更新...");
            var newRelease = await session.CheckAsync();
            if (newRelease == null)
                return UpdateStartupResult.Continue;

            stage = LauncherUpdateStage.Downloading;
            loadingDialog?.SetMessage("正在下载启动器更新...");
            loadingDialog?.ReportProgress(0);
            await session.DownloadAsync(newRelease, progress => loadingDialog?.ReportProgress(progress));

            if (changelogWindow != null)
            {
                try
                {
                    await changelogWindow.Dispatcher.InvokeAsync(() =>
                    {
                        beforeShowChangelog?.Invoke();
                        changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                        changelogWindow.ChangeLogText.Markdown = newRelease.TargetFullRelease.NotesMarkdown;
                        changelogWindow.ShowDialog();
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "无法显示更新日志窗口，继续进入更新安装流程");
                }
            }

            // Applying is deliberately outside the changelog catch: never invoke it twice on failure.
            stage = LauncherUpdateStage.Applying;
            session.ApplyAndRestart(newRelease);
            return UpdateStartupResult.Restarting;
        }
        catch (Exception ex)
        {
            throw new LauncherUpdateException(stage, ex);
        }
    }

    internal static GitHubSource CreateUpdateSource(bool downloadPrerelease, IFileDownloader? downloader = null) =>
        new(Links.REPO_URL, downloadPrerelease, Links.LAUNCHER_GITHUB_PROXY_BASE_URL, downloader);

    internal static string GetUpdateFailureMessage(Exception exception) =>
        exception switch
        {
            TimeoutException timeoutException => timeoutException.Message,
            not null when exception.FindHttpRequestException() is { StatusCode: not null } httpRequestException => (int)httpRequestException.StatusCode switch
            {
                403 or 429 or 444 or 522 => $"更新源返回错误状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}",
                _                 => $"更新请求失败, 状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}"
            },
            OperationCanceledException => "更新请求已被取消。",
            _                          => exception.Message
        };
}
