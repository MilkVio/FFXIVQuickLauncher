using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;

namespace XIVLauncher.Login;

public interface ILoginWorkflowInteraction
{
    void ShowQrCode(byte[] qrBytes);

    void ShowVerificationCode(string code);

    void ShowLoginMessage(string message);

    void ShowDeviceProfileDebug(LoginType loginType, DeviceProfileSnapshot deviceProfile);

    string? PromptTextInput(string text, string caption, string initialText);

    string? PromptCaptchaInput(LoginCaptchaChallenge challenge);

    QrLoginDeviceProfileSelection PromptQrLoginDeviceProfileChoice(IReadOnlyList<XIVAccount> independentDeviceProfileAccounts);

    NewAccountDeviceProfileChoice PromptNewAccountDeviceProfileChoice();

    bool ConfigureTemporaryAccountDeviceProfile(XIVAccount account, AccountManager accountManager);

    void ShowError(string message);

    string? GetSavedWeGameLauncherPath();

    void SaveWeGameLauncherPath(string path);

    string? PromptWeGameInstallDirectory(string? currentPath);

    Task<bool> TryElevatedCopyVersionDllAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
