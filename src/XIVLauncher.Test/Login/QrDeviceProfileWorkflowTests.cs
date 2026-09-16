using System.IO;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Login.Workflow;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.Login;

public sealed class QrDeviceProfileWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "VioletLoginTests", Guid.NewGuid().ToString("N"));
    private readonly LauncherSettingsV3 settings = new();
    private readonly AccountManager accounts;
    private static readonly LoginArea Area = new() { AreaID = "0", AreaName = "Test" };

    public QrDeviceProfileWorkflowTests() => accounts = new AccountManager(settings, directory);

    [Theory]
    [InlineData(NewAccountDeviceProfileChoice.CreateIndependent, false)]
    [InlineData(NewAccountDeviceProfileChoice.CreateIndependent, true)]
    [InlineData(NewAccountDeviceProfileChoice.UseShared, false)]
    [InlineData(NewAccountDeviceProfileChoice.UseShared, true)]
    [InlineData(NewAccountDeviceProfileChoice.UseExistingIndependent, false)]
    [InlineData(NewAccountDeviceProfileChoice.UseExistingIndependent, true)]
    public async Task SelectionBindsTheScannedAccountAndKeepsOneSnapshot(NewAccountDeviceProfileChoice choice, bool existingTarget)
    {
        await accounts.InitializeCredProviderAsync(CredType.NoEncryption);
        var source = SeedAccount("source");
        var sourceSnapshot = accounts.ResolveStoredIndependentDeviceProfile(source.ID)!.Snapshot;
        var sourcePresetId = source.DeviceProfilePresetId;
        var target = existingTarget ? SeedAccount("scanned") : null;
        if (target != null)
        {
            target.UserDefinedName = "Keep my remark";
            target.SdoPassword = "existing password";
            target.IsDeviceProfileRotation = false;
            target.DeviceProfileRotationDays = 31;
            accounts.Save(target);
        }

        var presetCount = accounts.GetDeviceProfilePresets().Count;
        var ui = new LoginUI { Selection = new(choice, source.ID) };
        var client = new LoginClientStub();
        client.BeforeLogin = () => Assert.Equal(presetCount, accounts.GetDeviceProfilePresets().Count);
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), client);
        var result = await workflow.ExecuteAsync(Request(ui));

        Assert.True(result!.IsAccountPersisted);
        Assert.Equal(!existingTarget, result.IsNewAccount);
        Assert.Equal(1, ui.PromptCount);
        var saved = accounts.FindAccount("scanned", XIVAccountType.Sdo)!;
        Assert.Equal("scanned", accounts.CurrentAccount!.UserName);
        Assert.True(saved.QuickLoginEnabled);
        Assert.Equal("new quick secret", saved.SdoQuickLoginSecret);
        Assert.Equal(sourcePresetId, source.DeviceProfilePresetId);
        Assert.Equal(sourceSnapshot, accounts.ResolveStoredIndependentDeviceProfile(source.ID)!.Snapshot);
        Assert.Equal(presetCount + (choice == NewAccountDeviceProfileChoice.CreateIndependent ? 1 : 0), accounts.GetDeviceProfilePresets().Count);

        var expected = choice switch
        {
            NewAccountDeviceProfileChoice.UseShared => accounts.GetSharedDeviceProfilePreset().ToSnapshot(),
            NewAccountDeviceProfileChoice.UseExistingIndependent => sourceSnapshot,
            _ => accounts.FindDeviceProfilePreset(saved.DeviceProfilePresetId)!.ToSnapshot()
        };
        Assert.Equal(choice != NewAccountDeviceProfileChoice.UseShared, saved.DeviceProfileDynamicEnabled);
        if (choice == NewAccountDeviceProfileChoice.UseExistingIndependent)
            Assert.Equal(sourcePresetId, saved.DeviceProfilePresetId);
        if (choice == NewAccountDeviceProfileChoice.CreateIndependent)
            Assert.NotEqual(sourcePresetId, saved.DeviceProfilePresetId);
        Assert.All(client.Requests, request => Assert.Equal(expected, request.DeviceProfile));
        Assert.Equal(client.Requests.Count, ui.DebugSnapshots.Count);
        Assert.All(ui.DebugSnapshots, snapshot => Assert.Equal(expected, snapshot));

        if (target != null)
        {
            Assert.Equal("Keep my remark", saved.UserDefinedName);
            Assert.Equal("existing password", saved.SdoPassword);
            Assert.False(saved.IsDeviceProfileRotation);
            Assert.Equal(31, saved.DeviceProfileRotationDays);
            Assert.Equal(1, saved.SortOrder);
        }

        client.BeforeLogin = null;
        Assert.Equal("session", await result.RefreshGameSessionIdByQuickLoginFunc!());
        Assert.Equal(expected, client.Requests[^1].DeviceProfile);
        Assert.Equal("scanned", client.Requests[^1].Account);
        Assert.Equal(expected, ui.DebugSnapshots[^1]);

        using var reloaded = new AccountManager(settings, directory);
        var stored = reloaded.FindAccount("scanned", XIVAccountType.Sdo)!;
        Assert.Equal(saved.DeviceProfilePresetId, stored.DeviceProfilePresetId);
        Assert.Equal(saved.DeviceProfileDynamicEnabled, stored.DeviceProfileDynamicEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QrPromptIsIndependentOfNewAccountPrompt(bool newAccountPrompt)
    {
        await accounts.InitializeCredProviderAsync(CredType.NoEncryption);
        SeedAccount("source");
        var ui = new LoginUI();
        var client = new LoginClientStub();
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), client);

        await workflow.ExecuteAsync(Request(ui, qrPrompt: false, newAccountPrompt: newAccountPrompt));

        Assert.Equal(0, ui.PromptCount);
        Assert.Single(client.Requests);
        Assert.Equal(string.Empty, client.Requests[0].Account);
        Assert.Equal(accounts.GetSharedDeviceProfilePreset().ToSnapshot(), client.Requests[0].DeviceProfile);
        Assert.False(accounts.FindAccount("scanned", XIVAccountType.Sdo)!.DeviceProfileDynamicEnabled);
    }

    [Theory]
    [InlineData(NewAccountDeviceProfileChoice.Cancel)]
    [InlineData(NewAccountDeviceProfileChoice.UseExistingIndependent)]
    public async Task CancelOrUnavailableSourceDoesNotSendLogin(NewAccountDeviceProfileChoice choice)
    {
        var initialPresets = accounts.GetDeviceProfilePresets().Count;
        var ui = new LoginUI { Selection = new(choice, "missing-account") };
        var client = new LoginClientStub();
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), client);

        Assert.Null(await workflow.ExecuteAsync(Request(ui)));
        Assert.Empty(client.Requests);
        Assert.Empty(accounts.Accounts);
        Assert.Equal(initialPresets, accounts.GetDeviceProfilePresets().Count);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("cancel")]
    [InlineData("missing-secret")]
    [InlineData("quick-login")]
    public async Task FailedLoginDoesNotPersistRandomPreset(string failure)
    {
        await accounts.InitializeCredProviderAsync(CredType.NoEncryption);
        var initialPresets = accounts.GetDeviceProfilePresets().Count;
        var ui = new LoginUI();
        var client = new LoginClientStub { Failure = failure };
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), client);

        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ExecuteAsync(Request(ui)));

        Assert.Empty(accounts.Accounts);
        Assert.Equal(initialPresets, accounts.GetDeviceProfilePresets().Count);
        Assert.Equal(string.Empty, settings.CurrentAccountID);
    }

    [Fact]
    public async Task CredentialEncryptionFailureDoesNotSaveAccountOrPreset()
    {
        var initialPresets = accounts.GetDeviceProfilePresets().Count;
        typeof(AccountManager).GetProperty(nameof(AccountManager.CredProvider))!.SetValue(accounts, new UnavailableCredentials());
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), new LoginClientStub());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ExecuteAsync(Request(new LoginUI())));

        Assert.Equal("保存快速登录凭据失败", error.Message);
        Assert.Empty(accounts.Accounts);
        Assert.Equal(initialPresets, accounts.GetDeviceProfilePresets().Count);
    }

    [Fact]
    public void GlobalRotationSwitchPreservesPerAccountPolicy()
    {
        var account = SeedAccount("source");
        var original = account.DeviceProfilePresetId;
        settings.DisableAllDeviceProfileRotation = true;
        Assert.Equal(original, accounts.ResolveDeviceProfile(account).PresetId);
        Assert.True(account.IsDeviceProfileRotation);

        settings.DisableAllDeviceProfileRotation = false;
        Assert.NotEqual(original, accounts.ResolveDeviceProfile(account).PresetId);
    }

    private XIVAccount SeedAccount(string name)
    {
        var account = new XIVAccount
        {
            AccountType = XIVAccountType.Sdo,
            SdoLoginAccount = name,
            AreaName = Area.AreaName,
            DeviceProfileDynamicEnabled = true,
            IsDeviceProfileRotation = true,
            DeviceProfileRotationDays = 7
        };
        account.GenerateID();
        accounts.ApplyDeviceProfileSelection(account, FakeMachineInfo.CreateSnapshot(), DateTimeOffset.UtcNow.AddDays(-60).UtcTicks, null);
        accounts.AddAccount(account);
        return account;
    }

    private static LoginWorkflowRequest Request(LoginUI ui, bool qrPrompt = true, bool newAccountPrompt = true) => new()
    {
        LoginType = LoginType.QRCode,
        Username = "source",
        Password = string.Empty,
        QuickLoginEnabled = false,
        ForceWeGameTokenRecapture = false,
        Action = LoginAfterAction.Start,
        CurrentArea = Area,
        LoginAreas = [Area],
        LoginCancellationTokenSource = new(),
        LoginSessionRefreshSink = null,
        Interaction = ui,
        RequireDeviceProfileSetupForNewLogin = newAccountPrompt,
        RequireDeviceProfileSetupForQRCodeLogin = qrPrompt,
        DeviceProfileDebugEnabled = true
    };

    public void Dispose()
    {
        accounts.Dispose();
        Directory.Delete(directory, true);
    }

    private sealed class NoWeGame : IWeGameTokenCaptureCoordinator
    {
        public Task<WeGameCaptureResult?> CaptureAsync(ILoginWorkflowUI interaction, CancellationTokenSource loginCancellationTokenSource) =>
            throw new InvalidOperationException("QR login must not start WeGame capture");
    }

    private sealed class UnavailableCredentials : ICredProvider
    {
        public string GetName() => "Unavailable test provider";
        public string GetDescription() => GetName();
        public Task<bool> IsSupported() => Task.FromResult(false);
        public Task ClearCache() => Task.CompletedTask;
        public Task Unregister() => Task.CompletedTask;
        public Task<string?> Encrypt(string? text) => Task.FromResult<string?>(null);
        public Task<string?> Decrypt(string? text) => Task.FromResult<string?>(null);
    }

    private sealed class LoginClientStub : ILoginClient
    {
        public List<LoginRequest> Requests { get; } = [];
        public Action? BeforeLogin { get; set; }
        public string? Failure { get; init; }

        public Task<LoginResult> LoginAsync(LoginType loginType, LoginRequest request, CancellationToken cancellationToken = default)
        {
            BeforeLogin?.Invoke();
            Requests.Add(request);
            if (Failure == "cancel") throw new OperationCanceledException();
            if (Failure == "network" || (Failure == "quick-login" && loginType == LoginType.QuickLogin)) throw new IOException("simulated failure");
            return Task.FromResult(new LoginResult
            {
                State = LoginState.Ok,
                OAuthLogin = new OAuthLoginResult
                {
                    InputUserID = "scanned",
                    SndaID = "scanned-id",
                    SessionID = "session",
                    QuickLoginSecret = Failure == "missing-secret" ? null : "new quick secret",
                    DeviceProfile = request.DeviceProfile
                }
            });
        }

        public Task<LoginResult> LoginBySessionKey(string account, string autoLoginSessionKey, ILoginSessionRefreshSink? loginSessionRefreshSink, DeviceProfileSnapshot deviceProfile) =>
            LoginAsync(LoginType.QuickLogin, new LoginRequest { Account = account, Secret = autoLoginSessionKey, DeviceProfile = deviceProfile });

        public Task<LoginResult> LoginWithFallback(LoginType loginType, LoginType fallbackLoginType, Func<LoginType, LoginRequest> requestFactory, CancellationToken cancellationToken = default) =>
            LoginAsync(loginType, requestFactory(loginType), cancellationToken);
    }

    private sealed class LoginUI : ILoginWorkflowUI
    {
        public QrLoginDeviceProfileSelection Selection { get; init; } = QrLoginDeviceProfileSelection.CreateIndependent;
        public int PromptCount { get; private set; }
        public List<DeviceProfileSnapshot> DebugSnapshots { get; } = [];
        public QrLoginDeviceProfileSelection PromptQrLoginDeviceProfileChoice(IReadOnlyList<XIVAccount> independentDeviceProfileAccounts)
        {
            PromptCount++;
            return Selection;
        }
        public void ShowDeviceProfileDebug(LoginType loginType, DeviceProfileSnapshot deviceProfile) => DebugSnapshots.Add(deviceProfile);
        public NewAccountDeviceProfileChoice PromptNewAccountDeviceProfileChoice() => throw new InvalidOperationException("Duplicate device prompt");
        public bool ConfigureTemporaryAccountDeviceProfile(XIVAccount account, AccountManager accountManager) => throw new NotSupportedException();
        public void ShowQRCode(byte[] qrBytes) { }
        public void ShowVerificationCode(string code) { }
        public void ShowLoginMessage(string message) { }
        public string? PromptTextInput(string text, string caption, string initialText) => throw new NotSupportedException();
        public string? PromptCaptchaInput(LoginCaptchaChallenge challenge) => throw new NotSupportedException();
        public void ShowError(string message) => throw new InvalidOperationException(message);
        public string? GetSavedWeGamePath() => null;
        public void SaveWeGamePath(string path) => throw new NotSupportedException();
        public string? PromptWeGameInstallDirectory(string? currentPath) => throw new NotSupportedException();
        public Task<bool> TryElevatedCopyVersionDllAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
