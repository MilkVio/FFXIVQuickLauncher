using System.Windows;
using Serilog;
using XIVLauncher.Windows;

namespace XIVLauncher.Update;

internal enum UpdateStartupResult { Continue, Exit, Restarting }
internal enum LauncherUpdateStage { Checking, Downloading, Applying }

internal sealed class LauncherUpdateException(LauncherUpdateStage stage, Exception cause)
    : Exception(cause.Message, cause)
{
    public LauncherUpdateStage Stage { get; } = stage;
}

internal static class UpdateStartupFlow
{
    // One recovery prompt for both setup failures and failures reported by the updater.
    internal static async Task<UpdateStartupResult> RunAsync(
        Func<Task<UpdateStartupResult>> check,
        Func<CustomMessageBox.Builder, MessageBoxResult> showMessage,
        Action closeWindows)
    {
        try
        {
            return await check();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动器更新未完成");
            var failure = ex as LauncherUpdateException ?? new LauncherUpdateException(LauncherUpdateStage.Checking, ex);
            var choice = showMessage(CreateFailurePrompt(failure));
            return failure.Stage != LauncherUpdateStage.Applying && choice == MessageBoxResult.Yes
                       ? UpdateStartupResult.Continue
                       : UpdateStartupResult.Exit;
        }
        finally
        {
            closeWindows();
        }
    }

    internal static CustomMessageBox.Builder CreateFailurePrompt(LauncherUpdateException failure)
    {
        var detail = UpdateOrchestrator.GetUpdateFailureMessage(failure.InnerException!);
        var applying = failure.Stage == LauncherUpdateStage.Applying;
        var title = failure.Stage switch
        {
            LauncherUpdateStage.Downloading => "无法下载启动器更新",
            LauncherUpdateStage.Applying => "无法完成启动器更新",
            _ => "无法检查启动器更新"
        };
        var description = applying
                              ? "安装更新时发生错误，请退出后重新启动启动器。"
                              : "更新服务暂时不可用，你可以继续使用当前版本。下次启动时会再次检查更新。";
        var builder = CustomMessageBox.Builder.NewFrom($"{title}\n\n{description}\n\n错误详情：{detail}")
            .WithCaption("XIVLauncherCN (Violet)")
            .WithImage(MessageBoxImage.Warning)
            .WithCancelResult(MessageBoxResult.No);
        return applying
                   ? builder.WithButtons(MessageBoxButton.OK).WithOkButtonText("退出").WithDefaultResult(MessageBoxResult.OK)
                   : builder.WithButtons(MessageBoxButton.YesNo).WithYesButtonText("继续启动启动器")
                       .WithNoButtonText("退出").WithDefaultResult(MessageBoxResult.Yes);
    }
}
