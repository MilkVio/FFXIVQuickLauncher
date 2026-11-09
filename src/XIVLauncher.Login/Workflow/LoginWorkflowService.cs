using System.Text.Json;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Channels;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Exceptions;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.WeGame;

namespace XIVLauncher.Login.Workflow;

public sealed class LoginWorkflowService
(
    AccountManager                 accountManager,
    IWeGameTokenCaptureCoordinator weGameTokenCaptureCoordinator,
    ILoginClient?                  client = null
)
{
    private readonly ILoginClient                       loginClient                        = client ?? new LoginClient();
    private readonly SavedAccountLoginResolver          savedAccountLoginResolver          = new(accountManager, weGameTokenCaptureCoordinator);
    private readonly NewAccountDeviceProfileCoordinator newAccountDeviceProfileCoordinator = new(accountManager);

    public async Task<LoginWorkflowResult?> ExecuteAsync
    (
        LoginWorkflowRequest request
    )
    {
        if (request.IsAutomaticLogin)
            return await RestoreSavedSessionAsync(request).ConfigureAwait(false);

        var resolvedLoginState = await savedAccountLoginResolver.ResolveAsync(request);
        if (resolvedLoginState == null)
            return null;

        var deviceProfilePreparation = newAccountDeviceProfileCoordinator.Prepare(request, resolvedLoginState);
        if (deviceProfilePreparation == null)
            return null;

        var         deviceProfileSnapshot = deviceProfilePreparation.ResolvedDeviceProfile.Snapshot;
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
        catch (LoginException) when (resolvedLoginState is
                                     {
                                         RequestedLoginType: LoginType.WeGame,
                                         UsedSavedCredential: true,
                                         SavedAccount: not null
                                     })
        {
            resolvedLoginState.SavedAccount.WeGameQuickLoginSecret = null;
            accountManager.Save(resolvedLoginState.SavedAccount);
            throw;
        }
        catch (Exception ex) when (deviceProfilePreparation.PendingQrDeviceProfile is { UseShared: false })
        {
            var message = deviceProfilePreparation.PendingQrDeviceProfile.PresetId == null
                              ? "[LoginWorkflow] 二维码登录未完成，丢弃临时独立设备画像, DeviceIdPrefix={DeviceIdPrefix}"
                              : "[LoginWorkflow] 二维码登录未完成，未绑定既有独立设备画像, DeviceIdPrefix={DeviceIdPrefix}";

            Log.Information
            (
                ex,
                message,
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

        if (deviceProfilePreparation.RequiresNewAccountDeviceProfileSetup       &&
            resolvedLoginState.RequestedLoginType == LoginType.QRCode           &&
            loginResult is { State: LoginState.Ok, OAuthLogin: { } oAuthLogin } &&
            pendingNewAccount?.DeviceProfileDynamicEnabled == true)
        {
            if (string.IsNullOrWhiteSpace(oAuthLogin.QuickLoginSecret))
                throw new InvalidOperationException("扫码登录未返回可保存的快速登录凭据");

            loginResult = await LoginAsync
                          (
                              request,
                              LoginType.QuickLogin,
                              LoginType.QuickLogin,
                              oAuthLogin.InputUserID,
                              oAuthLogin.QuickLoginSecret!,
                              true,
                              deviceProfileSnapshot
                          ).ConfigureAwait(false);
        }

        Func<Task<string>>? refreshGameSessionIdByQuickLoginFunc = null;

        var isAccountPersisted = false;
        var isNewAccount       = false;

        if (loginResult.State == LoginState.Ok)
        {
            var existedBefore = accountManager.FindAccount(loginResult.OAuthLogin?.InputUserID, resolvedLoginState.AccountType) != null;

            var accountToSave = await SaveAccountAsync
                                 (
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
                isAccountPersisted = true;
                isNewAccount       = !existedBefore;

                var refreshSecret = accountToSave.AccountType == XIVAccountType.Sdo
                                        ? loginResult.OAuthLogin?.QuickLoginSecret
                                        : resolvedLoginState.Secret;
                if (accountToSave.QuickLoginEnabled && !string.IsNullOrWhiteSpace(refreshSecret))
                    refreshGameSessionIdByQuickLoginFunc = CreateQuickLoginRefresh(
                        request, accountToSave.AccountType, accountToSave.UserName, refreshSecret, deviceProfileSnapshot);
            }
        }

        return new LoginWorkflowResult
        {
            GameLaunchContext                    = new GameLaunchContext(loginResult, resolvedLoginState.Area, request.LoginAreas, resolvedLoginState.AccountType),
            IsAccountPersisted                   = isAccountPersisted,
            IsNewAccount                         = isNewAccount,
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
                            request.Interaction.ShowQRCode(qrBytes);
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

        var savedAccount = resolvedLoginState.SavedAccount;
        if (savedAccount != null &&
            !string.Equals(savedAccount.UserName, oAuthLogin.InputUserID, StringComparison.Ordinal))
            savedAccount = null;

        var existingAccount = accountManager.FindAccount(oAuthLogin.InputUserID, resolvedLoginState.AccountType)
                              ?? savedAccount;
        var deviceProfileAccount = pendingNewAccount ?? existingAccount;
        var accountToSave = new XIVAccount
        {
            QuickLoginEnabled                  = resolvedLoginState.RequestedLoginType == LoginType.WeGame || loginQuickLoginEnabled,
            SdoLoginAccount                    = oAuthLogin.InputUserID,
            WeGameLoginAccount                 = oAuthLogin.InputUserID,
            AccountType                        = resolvedLoginState.AccountType,
            AreaName                           = resolvedLoginState.Area.AreaName,
            UserDefinedName                    = existingAccount?.UserDefinedName ?? deviceProfileAccount?.UserDefinedName ?? null!,
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

        // Reusing another account's device must not replace the scanned account's rotation policy.
        if (existingAccount != null && pendingQrDeviceProfile != null)
        {
            accountToSave.IsDeviceProfileRotation   = existingAccount.IsDeviceProfileRotation;
            accountToSave.DeviceProfileRotationDays = existingAccount.DeviceProfileRotationDays;
        }

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
        if (accountToSave.QuickLoginEnabled &&
            oAuthLogin is { TGT: { Length: > 0 } tgt, Guid: { Length: > 0 } guid })
        {
            var session = new SavedLoginSession
            {
                AccountID     = accountToSave.ID,
                SndaID        = oAuthLogin.SndaID,
                TGT           = tgt,
                GUID          = guid,
                DeviceProfile = deviceProfileSnapshot
            };
            // This cache is optional: failure must not discard a successful QR login or its credentials.
            accountToSave.LoginSessionSecret = await EncryptSessionAsync(session).ConfigureAwait(false);
        }


        try
        {
            if (pendingQrDeviceProfile is { UseShared: false })
            {
                var existingPreset = accountManager.FindDeviceProfilePreset(pendingQrDeviceProfile.PresetId);
                if (existingPreset == null)
                    createdDeviceProfilePreset = accountManager.CreateDeviceProfilePreset(pendingQrDeviceProfile.Snapshot, pendingQrDeviceProfile.GeneratedUtcTicks, null);

                var targetPreset = existingPreset ?? createdDeviceProfilePreset!;

                accountToSave.DeviceProfileDynamicEnabled        = true;
                accountToSave.DeviceProfilePresetId              = targetPreset.Id;
                accountToSave.DeviceProfileLastGeneratedUtcTicks = pendingQrDeviceProfile.GeneratedUtcTicks > 0
                                                                        ? pendingQrDeviceProfile.GeneratedUtcTicks
                                                                        : targetPreset.GeneratedUtcTicks;

                if (existingPreset == null)
                {
                    Log.Information
                    (
                        existingAccount == null
                            ? "[LoginWorkflow] 扫码成功后创建正式预设并绑定新账号, 账号={Account}, PresetId={PresetId}, DeviceIdPrefix={DeviceIdPrefix}"
                            : "[LoginWorkflow] 扫码成功后创建正式预设并重新绑定已有账号, 账号={Account}, PresetId={PresetId}, DeviceIdPrefix={DeviceIdPrefix}",
                        accountToSave.UserName,
                        targetPreset.Id,
                        GetDeviceIdPrefix(pendingQrDeviceProfile.Snapshot)
                    );
                }
                else
                {
                    Log.Information
                    (
                        "[LoginWorkflow] 扫码成功后沿用既有账号独立预设, 账号={Account}, PresetId={PresetId}, SourceAccountId={SourceAccountId}, DeviceIdPrefix={DeviceIdPrefix}",
                        accountToSave.UserName,
                        targetPreset.Id,
                        pendingQrDeviceProfile.SourceAccountId,
                        GetDeviceIdPrefix(pendingQrDeviceProfile.Snapshot)
                    );
                }
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

    private async Task<LoginWorkflowResult?> RestoreSavedSessionAsync(LoginWorkflowRequest request)
    {
        if (accountManager.CurrentCredType == CredType.WindowsHello)
            return null;

        var account = accountManager.FindAccount(request.Username, request.LoginType.ToAccountType());
        if (account is not { QuickLoginEnabled: true, LoginSessionSecret.Length: > 0 } ||
            accountManager.HasUnavailableSecrets(account))
            return null;

        void InvalidateSession()
        {
            account.LoginSessionSecret = null;
            accountManager.Save(account);
        }

        try
        {
            var cancellationToken = request.LoginCancellationTokenSource.Token;
            cancellationToken.ThrowIfCancellationRequested();
            var json = await accountManager.Decrypt(account.LoginSessionSecret).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                InvalidateSession();
                return null;
            }

            var session = JsonSerializer.Deserialize<SavedLoginSession>(json);
            var deviceProfile = accountManager.ResolveDeviceProfile(account).Snapshot;
            if (session == null ||
                !string.Equals(session.AccountID, account.ID, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(session.SndaID) ||
                string.IsNullOrWhiteSpace(session.TGT) ||
                string.IsNullOrWhiteSpace(session.GUID) ||
                !deviceProfile.Equals(session.DeviceProfile))
            {
                InvalidateSession();
                return null;
            }

            ShowDeviceProfileDebugIfNeeded(request, LoginType.QuickLogin, deviceProfile);
            cancellationToken.ThrowIfCancellationRequested();
            // Start the network deadline after the optional user-controlled debug dialog.
            request.LoginCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(15));
            var loginResult = await loginClient.RestoreSessionAsync(
                request.LoginType,
                new LoginRequest
                {
                    Account = account.UserName,
                    DeviceProfile = deviceProfile,
                    LoginSessionRefreshSink = request.LoginSessionRefreshSink
                },
                session.TGT, session.GUID, session.SndaID, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (loginResult?.OAuthLogin is not { TGT.Length: > 0 } oauth ||
                !string.Equals(oauth.SndaID, session.SndaID, StringComparison.Ordinal))
            {
                InvalidateSession();
                return null;
            }

            session = session with { TGT = oauth.TGT };
            account.LoginSessionSecret = await EncryptSessionAsync(session).ConfigureAwait(false);
            accountManager.Save(account);

            var encryptedSecret = account.IsWeGame ? account.WeGameQuickLoginSecret : account.SdoQuickLoginSecret;
            var refreshSecret = await accountManager.Decrypt(encryptedSecret).ConfigureAwait(false);
            var refresh = string.IsNullOrWhiteSpace(refreshSecret) ? null : CreateQuickLoginRefresh(
                request, account.AccountType, account.UserName, refreshSecret, deviceProfile);

            Log.Information("[LoginWorkflow] 已恢复账号 {AccountID} 的登录会话", account.ID);
            return new LoginWorkflowResult
            {
                GameLaunchContext = new GameLaunchContext(loginResult, request.CurrentArea, request.LoginAreas, account.AccountType),
                IsAccountPersisted = true,
                IsNewAccount = false,
                RefreshGameSessionIdByQuickLoginFunc = refresh
            };
        }
        catch (Exception ex) when (ex is LoginException or JsonException)
        {
            InvalidateSession();
            throw;
        }
        finally
        {
            request.LoginCancellationTokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
            await accountManager.CredProvider.ClearCache().ConfigureAwait(false);
        }
    }

    private async Task<string?> EncryptSessionAsync(SavedLoginSession session)
    {
        var secret = await accountManager.Encrypt(JsonSerializer.Serialize(session)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            Log.Warning("[LoginWorkflow] 无法保存自动恢复会话，下次启动需手动登录");
            return null;
        }
        return secret;
    }

    private Func<Task<string>> CreateQuickLoginRefresh(
        LoginWorkflowRequest request,
        XIVAccountType accountType,
        string username,
        string secret,
        DeviceProfileSnapshot deviceProfile) => async () =>
    {
        ShowDeviceProfileDebugIfNeeded(request, LoginType.QuickLogin, deviceProfile);
        var result = accountType == XIVAccountType.Sdo
            ? await loginClient.LoginBySessionKey(username, secret, request.LoginSessionRefreshSink, deviceProfile).ConfigureAwait(false)
            : await loginClient.LoginAsync(LoginType.WeGame, new LoginRequest
            {
                Account = username,
                Secret = secret,
                QuickLoginEnabled = false,
                DeviceProfile = deviceProfile,
                LoginSessionRefreshSink = request.LoginSessionRefreshSink
            }).ConfigureAwait(false);
        return await ResolveSessionIdFromResultAsync(result, deviceProfile).ConfigureAwait(false);
    };

    /// <summary>
    ///     从登录结果中解析 session ticket。
    ///     如果 SessionID 为空但 TGT 存在（延迟模式），则通过 TGT 实时获取。
    ///     用于 DC Travel 的 session 刷新回调。
    /// </summary>
    private static async Task<string> ResolveSessionIdFromResultAsync
    (
        LoginResult           loginResult,
        DeviceProfileSnapshot deviceProfile
    )
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
