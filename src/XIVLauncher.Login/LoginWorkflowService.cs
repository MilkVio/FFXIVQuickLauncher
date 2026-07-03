using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Channels;

namespace XIVLauncher.Login;

public sealed class LoginWorkflowService
(
    AccountManager                 accountManager,
    IWeGameTokenCaptureCoordinator weGameTokenCaptureCoordinator
)
{
    private readonly LoginClient                        loginClient                        = new();
    private readonly SavedAccountLoginResolver          savedAccountLoginResolver          = new(accountManager, weGameTokenCaptureCoordinator);
    private readonly NewAccountDeviceProfileCoordinator newAccountDeviceProfileCoordinator = new(accountManager);

    public async Task<LoginWorkflowResult?> ExecuteAsync(LoginWorkflowRequest request)
    {
        var resolvedLoginState = await savedAccountLoginResolver.ResolveAsync(request);
        if (resolvedLoginState == null)
            return null;

        var deviceProfilePreparation = newAccountDeviceProfileCoordinator.Prepare(request, resolvedLoginState);
        if (deviceProfilePreparation == null)
            return null;

        var deviceProfileSnapshot = deviceProfilePreparation.ResolvedDeviceProfile.Snapshot;
        LoginResult loginResult;
        try
        {
            loginResult = await LoginAsync
                          (
                              request,
                              resolvedLoginState.FinalLoginType,
                              resolvedLoginState.RequestedLoginType,
                              resolvedLoginState.Username,
                              resolvedLoginState.Secret,
                              deviceProfilePreparation.LoginQuickLoginEnabled,
                              deviceProfileSnapshot
                          ).ConfigureAwait(false);
        }
        catch (Exception ex) when (deviceProfilePreparation.PendingQrDeviceProfile is { UseShared: false })
        {
            Log.Information
            (
                ex,
                "[LoginWorkflow] 二维码登录未完成，丢弃临时独立设备画像, DeviceIdPrefix={DeviceIdPrefix}",
                GetDeviceIdPrefix(deviceProfileSnapshot)
            );
            throw;
        }

        var postQrResult = newAccountDeviceProfileCoordinator.ApplyAfterQrLogin(request, deviceProfilePreparation, resolvedLoginState, loginResult);
        if (postQrResult == null)
            return null;

        loginResult = postQrResult.LoginResult;
        var resolvedDeviceProfile = postQrResult.ResolvedDeviceProfile;
        var pendingNewAccount     = postQrResult.PendingNewAccount;
        deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;

        Func<Task<string>>? refreshGameSessionIdByQuickLoginFunc = null;
        var                 shouldShowQuickLoginDisclaimer       = false;
        var                 isAccountPersisted                   = false;

        if (loginResult.State == LoginState.Ok)
        {
            var accountToSave = await SaveAccountAsync
                                 (
                                     request,
                                     resolvedLoginState,
                                     resolvedDeviceProfile,
                                     pendingNewAccount,
                                     postQrResult.PendingQrDeviceProfile,
                                     deviceProfilePreparation.LoginQuickLoginEnabled,
                                     loginResult,
                                     deviceProfileSnapshot
                                 ).ConfigureAwait(false);

            if (accountToSave != null)
            {
                isAccountPersisted             = true;
                shouldShowQuickLoginDisclaimer = accountToSave.QuickLoginEnabled && resolvedLoginState.SavedAccount?.QuickLoginEnabled != true;

                if (accountToSave.AccountType == XIVAccountType.Sdo
                    && accountToSave.QuickLoginEnabled
                    && loginResult.OAuthLogin?.QuickLoginSecret is { Length: > 0 } autoLoginSessionKey)
                {
                    var refreshUsername     = accountToSave.SdoLoginAccount;
                    var cachedDeviceProfile = deviceProfileSnapshot;
                    refreshGameSessionIdByQuickLoginFunc = async () =>
                    {
                        ShowDeviceProfileDebugIfNeeded(request, LoginType.QuickLogin, cachedDeviceProfile);
                        var newLoginResult = await loginClient.LoginBySessionKey
                                             (
                                                 refreshUsername,
                                                 autoLoginSessionKey,
                                                 request.LoginSessionRefreshSink,
                                                 cachedDeviceProfile
                                             ).ConfigureAwait(false);
                        return await ResolveSessionIdFromResultAsync(newLoginResult, cachedDeviceProfile).ConfigureAwait(false);
                    };
                }
                else if (accountToSave.AccountType == XIVAccountType.WeGame
                         && accountToSave.QuickLoginEnabled
                         && resolvedLoginState.FinalLoginType == LoginType.WeGame
                         && loginResult.OAuthLogin?.InputUserID is { Length: > 0 } inputUserId
                         && resolvedLoginState.Secret is { Length: > 0 } weGameToken)
                {
                    var cachedDeviceProfile = deviceProfileSnapshot;
                    refreshGameSessionIdByQuickLoginFunc = async () =>
                    {
                        var newLoginResult = await loginClient.LoginAsync
                                             (
                                                 LoginType.WeGame,
                                                 new LoginRequest
                                                 {
                                                     Account                 = inputUserId,
                                                     Secret                  = weGameToken,
                                                     QuickLoginEnabled       = false,
                                                     DeviceProfile           = cachedDeviceProfile,
                                                     LoginSessionRefreshSink = request.LoginSessionRefreshSink
                                                 }
                                             ).ConfigureAwait(false);
                        return await ResolveSessionIdFromResultAsync(newLoginResult, cachedDeviceProfile).ConfigureAwait(false);
                    };
                }

            }
        }

        return new LoginWorkflowResult
        {
            GameLaunchContext                    = new GameLaunchContext(loginResult, resolvedLoginState.Area, request.LoginAreas),
            IsAccountPersisted                   = isAccountPersisted,
            UsedSavedWeGameToken                 = resolvedLoginState.RequestedLoginType == LoginType.WeGame && resolvedLoginState.UsedSavedCredential,
            RefreshGameSessionIdByQuickLoginFunc = refreshGameSessionIdByQuickLoginFunc
        };
    }

    private Task<LoginResult> LoginAsync
    (
        LoginWorkflowRequest  request,
        LoginType             type,
        LoginType             fallbackLoginType,
        string                username,
        string                secret,
        bool                  quickLoginEnabled,
        DeviceProfileSnapshot deviceProfile
    )
    {
        return loginClient.LoginWithFallback
        (
            type,
            fallbackLoginType,
            requestLoginType =>
            {
                ShowDeviceProfileDebugIfNeeded(request, requestLoginType, deviceProfile);

                return LoginRequest.Create
                (
                    username,
                    secret,
                    quickLoginEnabled,
                    deviceProfile,
                    request.LoginSessionRefreshSink,
                    request.LoginCancellationTokenSource,
                    qrBytes =>
                    {
                        if (requestLoginType == LoginType.QRCode)
                            request.Interaction.ShowQrCode(qrBytes);
                    },
                    code =>
                    {
                        if (requestLoginType == LoginType.Slide)
                            request.Interaction.ShowVerificationCode(code);
                    },
                    request.Interaction.ShowLoginMessage,
                    request.Interaction.PromptTextInput,
                    request.Interaction.PromptCaptchaInput
                );
            },
            request.LoginCancellationTokenSource.Token
        );
    }

    private static void ShowDeviceProfileDebugIfNeeded(LoginWorkflowRequest request, LoginType loginType, DeviceProfileSnapshot deviceProfile)
    {
        if (!request.DeviceProfileDebugEnabled || loginType is not (LoginType.QRCode or LoginType.QuickLogin))
            return;

        request.Interaction.ShowDeviceProfileDebug(loginType, deviceProfile);
    }

    private async Task<XIVAccount?> SaveAccountAsync
    (
        LoginWorkflowRequest  request,
        ResolvedLoginState    resolvedLoginState,
        ResolvedDeviceProfile resolvedDeviceProfile,
        XIVAccount?           pendingNewAccount,
        PendingQrDeviceProfile? pendingQrDeviceProfile,
        bool                    loginQuickLoginEnabled,
        LoginResult           loginResult,
        DeviceProfileSnapshot deviceProfileSnapshot
    )
    {
        var oAuthLogin = loginResult.OAuthLogin;
        if (oAuthLogin == null)
            return null;

        var existingAccount = accountManager.FindAccount(oAuthLogin.InputUserID, resolvedLoginState.AccountType)
                              ?? resolvedLoginState.SavedAccount;
        var deviceProfileAccount = pendingNewAccount ?? existingAccount;
        var accountToSave = new XIVAccount
        {
            QuickLoginEnabled                  = resolvedLoginState.RequestedLoginType == LoginType.WeGame || loginQuickLoginEnabled,
            SdoLoginAccount                    = oAuthLogin.InputUserID,
            WeGameLoginAccount                 = oAuthLogin.InputUserID,
            AccountType                        = resolvedLoginState.AccountType,
            AreaName                           = resolvedLoginState.Area.AreaName,
            UserDefinedName                    = deviceProfileAccount?.UserDefinedName                    ?? null!,
            SdoPassword                        = existingAccount?.SdoPassword                            ?? string.Empty,
            SdoQuickLoginSecret                = existingAccount?.SdoQuickLoginSecret                    ?? string.Empty,
            WeGameQuickLoginSecret             = existingAccount?.WeGameQuickLoginSecret,
            DeviceProfilePresetId              = deviceProfileAccount?.DeviceProfilePresetId              ?? string.Empty,
            DeviceProfileDynamicEnabled        = deviceProfileAccount?.DeviceProfileDynamicEnabled        ?? false,
            IsDeviceProfileRotation            = deviceProfileAccount?.IsDeviceProfileRotation            ?? true,
            DeviceProfileRotationDays          = deviceProfileAccount?.DeviceProfileRotationDays          ?? AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
            DeviceProfileLastGeneratedUtcTicks = deviceProfileAccount?.DeviceProfileLastGeneratedUtcTicks ?? 0,
            SortOrder                          = existingAccount?.SortOrder                              ?? 0
        };

        AccountManager.ApplyResolvedDeviceProfile(accountToSave, resolvedDeviceProfile);

        if (loginQuickLoginEnabled && accountToSave.AccountType == XIVAccountType.Sdo)
        {
            if (!string.IsNullOrEmpty(oAuthLogin.QuickLoginSecret))
                accountToSave.SdoQuickLoginSecret = await accountManager.Encrypt(oAuthLogin.QuickLoginSecret).ConfigureAwait(false) ?? throw new InvalidOperationException("保存快速登录凭据失败");
            else if (resolvedLoginState.RequestedLoginType == LoginType.QRCode)
                throw new InvalidOperationException("扫码登录未返回可保存的快速登录凭据");

            if (resolvedLoginState.FinalLoginType == LoginType.Static)
                accountToSave.SdoPassword = await accountManager.Encrypt(resolvedLoginState.Secret).ConfigureAwait(false) ?? throw new InvalidOperationException("保存账号密码失败");
        }

        if (resolvedLoginState.FinalLoginType == LoginType.WeGame)
        {
            accountToSave.WeGameQuickLoginSecret = await accountManager.Encrypt(resolvedLoginState.Secret).ConfigureAwait(false);
            Log.Information("[LoginWorkflow] WeGame 令牌已保存, 账号={Account}", accountToSave.WeGameLoginAccount);
        }

        DeviceProfilePreset? createdDeviceProfilePreset = null;
        accountToSave.GenerateID();

        try
        {
            if (pendingQrDeviceProfile is { UseShared: false })
            {
                createdDeviceProfilePreset = accountManager.CreateDeviceProfilePreset(pendingQrDeviceProfile.Snapshot, pendingQrDeviceProfile.GeneratedUtcTicks, null);
                accountToSave.DeviceProfileDynamicEnabled        = true;
                accountToSave.DeviceProfilePresetId              = createdDeviceProfilePreset.Id;
                accountToSave.DeviceProfileLastGeneratedUtcTicks = pendingQrDeviceProfile.GeneratedUtcTicks;

                Log.Information
                (
                    existingAccount == null
                        ? "[LoginWorkflow] 扫码成功后创建正式预设并绑定新账号, 账号={Account}, PresetId={PresetId}, DeviceIdPrefix={DeviceIdPrefix}"
                        : "[LoginWorkflow] 扫码成功后创建正式预设并重新绑定已有账号, 账号={Account}, PresetId={PresetId}, DeviceIdPrefix={DeviceIdPrefix}",
                    accountToSave.UserName,
                    createdDeviceProfilePreset.Id,
                    GetDeviceIdPrefix(pendingQrDeviceProfile.Snapshot)
                );
            }
            else if (pendingQrDeviceProfile is { UseShared: true })
            {
                accountToSave.DeviceProfileDynamicEnabled = false;
                Log.Information("[LoginWorkflow] 扫码成功后保存共享设备画像账号, 账号={Account}", accountToSave.UserName);
            }

            accountManager.AddAccount(accountToSave);
            accountManager.CurrentAccount = accountToSave;
            accountManager.Save();
            await accountManager.CredProvider.ClearCache().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (createdDeviceProfilePreset != null)
            {
                var cleaned = accountManager.TryDeleteUnreferencedDeviceProfilePreset(createdDeviceProfilePreset.Id);
                Log.Warning
                (
                    ex,
                    "[LoginWorkflow] 账号保存失败，已尝试清理本次创建的孤立设备预设, PresetId={PresetId}, Cleaned={Cleaned}",
                    createdDeviceProfilePreset.Id,
                    cleaned
                );
            }

            throw;
        }

        return accountToSave;
    }

    /// <summary>
    ///     从登录结果中解析 session ticket。
    ///     如果 SessionID 为空但 TGT 存在（延迟模式），则通过 TGT 实时获取。
    ///     用于 DC Travel 的 session 刷新回调。
    /// </summary>
    private static async Task<string> ResolveSessionIdFromResultAsync(LoginResult loginResult, DeviceProfileSnapshot deviceProfile)
    {
        var oauth = loginResult.OAuthLogin;
        if (oauth == null)
            return string.Empty;

        if (!string.IsNullOrEmpty(oauth.SessionID))
            return oauth.SessionID;

        if (!string.IsNullOrEmpty(oauth.TGT) && !string.IsNullOrEmpty(oauth.Guid))
        {
            var loginCtx = new LoginChannelContext(deviceProfile);
            return await loginCtx.GetSessionIdAsync(oauth.TGT, oauth.Guid).ConfigureAwait(false);
        }

        return string.Empty;
    }

    private static string GetDeviceIdPrefix(DeviceProfileSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.DeviceId)
            ? string.Empty
            : snapshot.DeviceId[..Math.Min(8, snapshot.DeviceId.Length)];
}
