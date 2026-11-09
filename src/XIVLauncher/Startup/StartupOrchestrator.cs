using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommandLine;
using Serilog;
using Serilog.Events;
using Velopack;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Update;
using XIVLauncher.Windows;
using DateTimeOffset = System.DateTimeOffset;

namespace XIVLauncher.Startup;

public class StartupOrchestrator
(
    Dispatcher dispatcher
)
{
    private readonly StartupContext context = new()
    {
        Dispatcher = dispatcher
    };

    private CommandLineOptions          commandLineOptions = new();
    private StartupDalamudProgressSink? dalamudProgressSink;

    private LoadingDialog? updateWindow;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        InitializeLogging();
        ParseCommandLine();

        Log.Information("开始执行启动流程");

        cancellationToken.ThrowIfCancellationRequested();
        await InitializeSettingsAsync();

        cancellationToken.ThrowIfCancellationRequested();
        InitializeVelopack();

#if RELEASE
        cancellationToken.ThrowIfCancellationRequested();
        await CheckUpdatesAsync();
#endif

        if (context.IsRestartingForUpdate || context.IsStartupCancelled)
        {
            Log.Information("已退出启动流程，更新重启={Restarting}", context.IsRestartingForUpdate);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        InitializeDalamud();

        Log.Information("启动流程完成");
    }

    public StartupContext GetContext() => context;

    private static void InitializeLogging()
    {
        try
        {
            LogInit.Setup
            (
                Path.Combine(Paths.RoamingPath, "output.log"),
                Environment.GetCommandLineArgs()
            );

            Log.Information("========================================================");
            Log.Information("启动会话 (v{Version} - {Hash})", AppUtil.GetAssemblyVersion(), AppUtil.GetGitHash());

            SerilogEventSink.Instance.LogLine += OnSerilogLogLine;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法设置日志记录, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Violet)", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private static void OnSerilogLogLine
    (
        object?                                                                            sender,
        (string Line, LogEventLevel Level, DateTimeOffset TimeStamp, Exception? Exception) e
    )
    {
        if (e.Exception == null)
            return;

        Troubleshooting.LogException(e.Exception, e.Line);
    }

    private void ParseCommandLine()
    {
        try
        {
            var helpWriter = new StringWriter();
            var parser = new Parser
            (config =>
                {
                    config.HelpWriter             = helpWriter;
                    config.IgnoreUnknownArguments = true;
                }
            );
            var result = parser.ParseArguments<CommandLineOptions>(Environment.GetCommandLineArgs());

            if (result.Errors.Any())
                MessageBox.Show(helpWriter.ToString(), "帮助");

            commandLineOptions = result.Value ?? new();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法解析命令行参数, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Violet)", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private async Task InitializeSettingsAsync()
    {
        context.Settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

        if (LogInit.LevelSwitch != null && context.Settings.EnableVerboseLog)
            LogInit.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;

        if (LogInit.LevelSwitch != null)
            Log.Information("当前日志级别: {LevelSwitchMinimumLevel}", LogInit.LevelSwitch.MinimumLevel);
        else
            Log.Information("当前日志级别: 未初始化");

        try
        {
            if (!string.IsNullOrEmpty(commandLineOptions.RoamingPath))
                Paths.OverrideRoamingPath(commandLineOptions.RoamingPath);

            context.Settings.Update
            (settings =>
                {
                    settings.EnableVerboseLog = false;

                    if (!string.IsNullOrEmpty(commandLineOptions.AccountName))
                    {
                        settings.CurrentAccountID = commandLineOptions.AccountName;
                        Log.Verbose("账号覆盖: '{AccountName}'", commandLineOptions.AccountName);
                    }
                }
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法应用命令行设置覆盖");
        }

        context.AccountManager = new AccountManager(context.Settings);

        var credTypeApplyResult = await context.AccountManager.InitializeCredProviderAsync(context.Settings.CredType);
        context.CredTypeApplyResult = credTypeApplyResult;

        if (!credTypeApplyResult.Succeeded)
            throw new InvalidOperationException(credTypeApplyResult.UserMessage ?? "自动登录加密方式初始化失败");

        if (credTypeApplyResult.WasFallbackApplied)
            context.Settings.CredType = credTypeApplyResult.AppliedCredType;

        context.Settings.Save();
    }

    private static void InitializeVelopack() =>
        VelopackApp.Build().Run();

    private async Task CheckUpdatesAsync()
    {
        ChangelogWindow? changelogWindow = null;
        var result = await UpdateStartupFlow.RunAsync(
            async () =>
            {
                Log.Information("开始检查启动器更新");
                updateWindow = new LoadingDialog();
                updateWindow.Show();
                try
                {
                    changelogWindow = new ChangelogWindow();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "无法创建更新日志窗口");
                }
                return await new UpdateOrchestrator().Run(false, updateWindow, changelogWindow, CloseUpdateWindow);
            },
            builder => context.Dispatcher.Invoke(() => builder.WithParentWindow(updateWindow!).Show()),
            () => context.Dispatcher.Invoke(() =>
            {
                CloseUpdateWindow();
                changelogWindow?.Close();
            }));
        context.IsRestartingForUpdate = result == UpdateStartupResult.Restarting;
        context.IsStartupCancelled = result == UpdateStartupResult.Exit;
    }

    private void CloseUpdateWindow()
    {
        void CloseCore()
        {
            if (updateWindow == null)
                return;

            updateWindow.Close();
            updateWindow = null;
        }

        if (context.Dispatcher.CheckAccess())
        {
            CloseCore();
            return;
        }

        context.Dispatcher.Invoke(CloseCore);
    }

    private void InitializeDalamud()
    {
        try
        {
            var dalamudWindowThread = new Thread(StartDalamudOverlayThread);
            dalamudWindowThread.SetApartmentState(ApartmentState.STA);
            dalamudWindowThread.IsBackground = true;
            dalamudWindowThread.Start();

            while (dalamudProgressSink == null)
                Thread.Yield();

            context.Dalamud = new DalamudService
            (
                new DalamudHostPaths
                (
                    new(Path.Combine(Paths.RoamingPath, "addon")),
                    new(Path.Combine(Paths.RoamingPath, "runtime")),
                    new(Path.Combine(Paths.RoamingPath, "dalamudAssets")),
                    new(Paths.RoamingPath),
                    new(Paths.RoamingPath),
                    new(Environment.CurrentDirectory)
                ),
                dalamudProgressSink,
                new AppDalamudGameVersionProvider(),
                new AppDalamudTroubleshootingProvider()
            );

            context.Dalamud.RunUpdater();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法启动 Dalamud 更新器");
            throw;
        }
    }

    private void StartDalamudOverlayThread()
    {
        var overlay = new LoadingDialog("正在更新 Dalamud 框架...", true);
        overlay.Hide();
        dalamudProgressSink = new StartupDalamudProgressSink(overlay);

        Dispatcher.Run();
    }
}
