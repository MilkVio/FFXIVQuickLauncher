using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

internal sealed class NewAccountDeviceProfileCoordinator
(
    AccountManager accountManager
)
{
    public DeviceProfilePreparation? Prepare(LoginWorkflowRequest request, ResolvedLoginState resolvedLoginState)
    {
        if (resolvedLoginState.RequestedLoginType == LoginType.QRCode)
            return PrepareQrLogin(request, resolvedLoginState);

        var requiresNewAccountDeviceProfileSetup = request.RequireDeviceProfileSetupForNewLogin && resolvedLoginState.SavedAccount == null;
        var loginQuickLoginEnabled               = resolvedLoginState.QuickLoginEnabled;

        if (!requiresNewAccountDeviceProfileSetup)
        {
            var resolvedDeviceProfile = accountManager.ResolveDeviceProfile(resolvedLoginState.Username, resolvedLoginState.AccountType);
            return new DeviceProfilePreparation
            (
                resolvedDeviceProfile,
                null,
                requiresNewAccountDeviceProfileSetup,
                loginQuickLoginEnabled,
                null
            );
        }

        var pendingNewAccount = CreatePendingNewAccount
        (
            resolvedLoginState.Username,
            resolvedLoginState.Username,
            resolvedLoginState.AccountType,
            resolvedLoginState.Area
        );

        switch (request.Interaction.PromptNewAccountDeviceProfileChoice())
        {
            case NewAccountDeviceProfileChoice.UseShared:
                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(pendingNewAccount),
                    pendingNewAccount,
                    true,
                    loginQuickLoginEnabled,
                    null
                );

            case NewAccountDeviceProfileChoice.ConfigurePerAccount:
            {
                var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);

                if (!request.Interaction.ConfigureTemporaryAccountDeviceProfile(configuredNewAccount, accountManager))
                {
                    SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                    return null;
                }

                return new DeviceProfilePreparation
                (
                    accountManager.ResolveDeviceProfile(configuredNewAccount),
                    configuredNewAccount,
                    true,
                    loginQuickLoginEnabled,
                    null
                );
            }

            default:
                return null;
        }
    }

    public PostQrDeviceProfileResult? ApplyAfterQrLogin(LoginWorkflowRequest request, DeviceProfilePreparation preparation, ResolvedLoginState resolvedLoginState, LoginResult loginResult)
    {
        if (resolvedLoginState.RequestedLoginType != LoginType.QRCode || loginResult.State != LoginState.Ok || loginResult.OAuthLogin == null)
            return new PostQrDeviceProfileResult(loginResult, preparation.ResolvedDeviceProfile, preparation.PendingNewAccount, preparation.PendingQrDeviceProfile);

        if (preparation.PendingQrDeviceProfile == null)
            return new PostQrDeviceProfileResult(loginResult, preparation.ResolvedDeviceProfile, preparation.PendingNewAccount, null);

        var oAuthLogin = loginResult.OAuthLogin;
        var pendingNewAccount = preparation.PendingNewAccount
                                ?? CreatePendingNewAccount(oAuthLogin.InputUserID, oAuthLogin.SndaID, resolvedLoginState.AccountType, resolvedLoginState.Area);

        if (preparation.PendingQrDeviceProfile.UseShared)
        {
            pendingNewAccount.DeviceProfileDynamicEnabled = false;
            Log.Information("[LoginWorkflow] 二维码登录扫码成功后保持共享设备画像, 账号={Account}", oAuthLogin.InputUserID);
            return new PostQrDeviceProfileResult(loginResult, preparation.ResolvedDeviceProfile, pendingNewAccount, preparation.PendingQrDeviceProfile);
        }

        var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);
        configuredNewAccount.DeviceProfileLastGeneratedUtcTicks = preparation.PendingQrDeviceProfile.GeneratedUtcTicks;

        var resolvedDeviceProfile = new ResolvedDeviceProfile
        (
            preparation.PendingQrDeviceProfile.Snapshot,
            null,
            true,
            configuredNewAccount.IsDeviceProfileRotation,
            configuredNewAccount.DeviceProfileRotationDays,
            preparation.PendingQrDeviceProfile.GeneratedUtcTicks
        );

        Log.Information
        (
            "[LoginWorkflow] 二维码登录扫码成功后准备绑定临时独立画像, 账号={Account}, DeviceIdPrefix={DeviceIdPrefix}",
            oAuthLogin.InputUserID,
            GetDeviceIdPrefix(preparation.PendingQrDeviceProfile.Snapshot)
        );

        return new PostQrDeviceProfileResult(loginResult, resolvedDeviceProfile, configuredNewAccount, preparation.PendingQrDeviceProfile);
    }

    private DeviceProfilePreparation? PrepareQrLogin(LoginWorkflowRequest request, ResolvedLoginState resolvedLoginState)
    {
        if (!request.RequireDeviceProfileSetupForNewLogin)
        {
            var resolvedDeviceProfile = accountManager.ResolveDeviceProfile(resolvedLoginState.Username, resolvedLoginState.AccountType);
            return new DeviceProfilePreparation
            (
                resolvedDeviceProfile,
                null,
                false,
                resolvedLoginState.QuickLoginEnabled,
                null
            );
        }

        switch (request.Interaction.PromptQrLoginDeviceProfileChoice())
        {
            case NewAccountDeviceProfileChoice.UseShared:
            {
                var resolvedDeviceProfile = accountManager.ResolveDeviceProfile(null, resolvedLoginState.AccountType);
                var pendingQrDeviceProfile = new PendingQrDeviceProfile
                (
                    resolvedDeviceProfile.Snapshot,
                    true,
                    resolvedDeviceProfile.LastGeneratedUtcTicks
                );

                Log.Information("[LoginWorkflow] 二维码登录选择共享设备画像");
                return new DeviceProfilePreparation
                (
                    resolvedDeviceProfile,
                    null,
                    true,
                    true,
                    pendingQrDeviceProfile
                );
            }

            case NewAccountDeviceProfileChoice.CreateIndependent:
            {
                var generatedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                var snapshot          = FakeMachineInfo.CreateSnapshot();
                var resolvedDeviceProfile = new ResolvedDeviceProfile
                (
                    snapshot,
                    null,
                    true,
                    true,
                    AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
                    generatedUtcTicks
                );
                var pendingQrDeviceProfile = new PendingQrDeviceProfile(snapshot, false, generatedUtcTicks);

                Log.Information
                (
                    "[LoginWorkflow] 二维码登录生成临时独立设备画像, DeviceIdPrefix={DeviceIdPrefix}, GeneratedUtcTicks={GeneratedUtcTicks}",
                    GetDeviceIdPrefix(snapshot),
                    generatedUtcTicks
                );

                return new DeviceProfilePreparation
                (
                    resolvedDeviceProfile,
                    null,
                    true,
                    true,
                    pendingQrDeviceProfile
                );
            }

            default:
                Log.Information("[LoginWorkflow] 二维码登录设备画像选择已取消");
                return null;
        }
    }

    private static XIVAccount CreatePendingNewAccount(string loginAccount, string sndaId, XIVAccountType accountType, LoginArea area) =>
        new()
        {
            SdoLoginAccount             = loginAccount,
            WeGameLoginAccount          = loginAccount,
            AccountType                 = accountType,
            AreaName                    = area.AreaName,
            DeviceProfilePresetId       = string.Empty,
            DeviceProfileDynamicEnabled = false,
            IsDeviceProfileRotation     = true,
            DeviceProfileRotationDays   = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS
        };

    private static XIVAccount CreateIndependentDeviceProfileDraft(XIVAccount account) =>
        new()
        {
            SdoLoginAccount                    = account.SdoLoginAccount,
            WeGameLoginAccount                 = account.WeGameLoginAccount,
            AccountType                        = account.AccountType,
            AreaName                           = account.AreaName,
            UserDefinedName                    = account.UserDefinedName,
            DeviceProfilePresetId              = account.DeviceProfilePresetId,
            DeviceProfileDynamicEnabled        = true,
            IsDeviceProfileRotation            = account.IsDeviceProfileRotation,
            DeviceProfileRotationDays          = account.DeviceProfileRotationDays,
            DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks
        };

    private void SavePendingNewAccountWithoutSecrets(XIVAccount account)
    {
        account.QuickLoginEnabled      = false;
        account.SdoQuickLoginSecret    = string.Empty;
        account.WeGameQuickLoginSecret = null;
        account.SdoPassword            = string.Empty;
        account.GenerateID();
        accountManager.AddAccount(account);
        accountManager.CurrentAccount = account;
        accountManager.Save();
    }

    private static string GetDeviceIdPrefix(DeviceProfileSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.DeviceId)
            ? string.Empty
            : snapshot.DeviceId[..Math.Min(8, snapshot.DeviceId.Length)];
}

internal sealed record PendingQrDeviceProfile
(
    DeviceProfileSnapshot Snapshot,
    bool                  UseShared,
    long                  GeneratedUtcTicks
);

internal sealed record DeviceProfilePreparation
(
    ResolvedDeviceProfile ResolvedDeviceProfile,
    XIVAccount?           PendingNewAccount,
    bool                  RequiresNewAccountDeviceProfileSetup,
    bool                  LoginQuickLoginEnabled,
    PendingQrDeviceProfile? PendingQrDeviceProfile
);

internal sealed record PostQrDeviceProfileResult
(
    LoginResult           LoginResult,
    ResolvedDeviceProfile ResolvedDeviceProfile,
    XIVAccount?           PendingNewAccount,
    PendingQrDeviceProfile? PendingQrDeviceProfile
);
