using System.IO;
using System.Text.Json;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class DeviceProfileSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "VioletSettingsTests", Guid.NewGuid().ToString("N"));

    public DeviceProfileSettingsTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacySwitchIsMigratedOnce(bool enabled)
    {
        var path = WriteConfig(JsonSerializer.Serialize(new { RequireDeviceProfileSetupForNewLogin = enabled }));
        var settings = LauncherSettingsV3.Load(path);

        Assert.Equal(enabled, settings.RequireDeviceProfileSetupForQRCodeLogin);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(enabled, document.RootElement.GetProperty("RequireDeviceProfileSetupForQRCodeLogin").GetBoolean());

        settings.RequireDeviceProfileSetupForNewLogin = !enabled;
        Assert.Equal(enabled, LauncherSettingsV3.Load(path).RequireDeviceProfileSetupForQRCodeLogin);
    }

    [Theory]
    [InlineData("RequireDeviceProfileSetupForQRCodeLogin", false)]
    [InlineData("requiredeviceprofilesetupforqrcodelogin", false)]
    [InlineData("RequireDeviceProfileSetupForQRCodeLogin", true)]
    public void ExplicitQrSettingIsPreserved(string key, bool enabled)
    {
        var path = WriteConfig(JsonSerializer.Serialize(new Dictionary<string, bool>
        {
            ["RequireDeviceProfileSetupForNewLogin"] = !enabled,
            [key] = enabled,
            ["DeviceProfileDebugEnabled"] = true,
            ["DisableAllDeviceProfileRotation"] = true
        }));
        var settings = LauncherSettingsV3.Load(path);

        Assert.Equal(enabled, settings.RequireDeviceProfileSetupForQRCodeLogin);
        Assert.True(settings.DeviceProfileDebugEnabled);
        Assert.True(settings.DisableAllDeviceProfileRotation);
    }

    [Fact]
    public void RecoveredBackupAlsoMigratesLegacySwitch()
    {
        var path = WriteConfig("invalid json");
        File.WriteAllText(path + ".bak", "{\"RequireDeviceProfileSetupForNewLogin\":true}");

        Assert.True(LauncherSettingsV3.Load(path).RequireDeviceProfileSetupForQRCodeLogin);
        Assert.True(LauncherSettingsV3.Load(path).RequireDeviceProfileSetupForQRCodeLogin);
    }

    [Fact]
    public void FreshSettingsKeepBothPromptsDisabled()
    {
        var settings = LauncherSettingsV3.Load(Path.Combine(directory, "new.json"));
        Assert.False(settings.RequireDeviceProfileSetupForNewLogin);
        Assert.False(settings.RequireDeviceProfileSetupForQRCodeLogin);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, true);
}
