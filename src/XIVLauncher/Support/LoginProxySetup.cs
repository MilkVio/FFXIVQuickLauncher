using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Support;

/// <summary>
///     把当前设置中的代理配置推送到 <see cref="LoginProxyPool"/>
/// </summary>
public static class LoginProxySetup
{
    public static string TestUrl => Links.SDO_LOGIN_AREA_URL;

    public static void ApplyFromSettings()
    {
        var settings = App.Settings;

        LoginProxyPool.Shared.Configure
        (
            settings.LoginProxyEnabled,
            settings.LoginProxyEntries,
            settings.LoginProxyPickMode,
            settings.LoginProxyManualEntryId,
            settings.LoginProxyLastWorkingEntryId,
            id => App.Settings.LoginProxyLastWorkingEntryId = id
        );
    }
}
