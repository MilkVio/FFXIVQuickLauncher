using System.IO;
using System.Text.Json;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Exceptions;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.Workflow;
using Xunit;

namespace XIVLauncher.Test.Login;

public sealed partial class QrDeviceProfileWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRenewsSessionAndRetainsDeviceDebugAndQuickLoginFallback(bool weGame)
    {
        var account = await SeedSession(weGame);
        var snapshot = accounts.ResolveDeviceProfile(account).Snapshot;
        var ui = new LoginUI();
        var client = new LoginClientStub { OnRestore = request => Assert.Equal(snapshot, Assert.Single(ui.DebugSnapshots)) };
        var workflow = new LoginWorkflowService(accounts, new NoWeGame(), client);
        using var cts = new CancellationTokenSource();

        var result = await workflow.ExecuteAsync(RestoreRequest(ui, cts, weGame));

        Assert.NotNull(result);
        Assert.Equal(1, client.RestoreCalls);
        Assert.Equal(0, ui.PromptCount);
        Assert.True(result.IsAccountPersisted);
        using var session = JsonDocument.Parse(account.LoginSessionSecret!);
        Assert.Equal("renewed-tgt", session.RootElement.GetProperty("TGT").GetString());
        Assert.NotNull(result.RefreshGameSessionIdByQuickLoginFunc);
        Assert.Equal("session", await result.RefreshGameSessionIdByQuickLoginFunc());
        Assert.Equal("source", client.Requests[^1].Account);
        Assert.Equal("saved-quick-secret", client.Requests[^1].Secret);
        Assert.All(client.Requests, request => Assert.Equal(snapshot, request.DeviceProfile));
        using var reloaded = new AccountManager(settings, directory);
        Assert.Equal(account.LoginSessionSecret, reloaded.FindAccount("source", account.AccountType)!.LoginSessionSecret);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("other-account")]
    [InlineData("missing-tgt")]
    [InlineData("other-device")]
    public async Task InvalidCacheIsClearedWithoutSendingRequest(string invalid)
    {
        var account = await SeedSession();
        account.LoginSessionSecret = invalid switch
        {
            "invalid-json" => "{",
            "other-account" => account.LoginSessionSecret!.Replace(account.ID, "other|Sdo"),
            "missing-tgt" => account.LoginSessionSecret!.Replace("cached-tgt", ""),
            _ => account.LoginSessionSecret!.Replace(accounts.ResolveDeviceProfile(account).Snapshot.DeviceId, "changed-device")
        };
        accounts.Save(account);
        var client = new LoginClientStub();
        using var cts = new CancellationTokenSource();
        var execute = () => new LoginWorkflowService(accounts, new NoWeGame(), client).ExecuteAsync(RestoreRequest(new LoginUI(), cts));
        if (invalid == "invalid-json")
            await Assert.ThrowsAsync<JsonException>(execute);
        else
            Assert.Null(await execute());
        Assert.Equal(0, client.RestoreCalls);
        Assert.Null(account.LoginSessionSecret);
        Assert.Equal("saved-quick-secret", account.SdoQuickLoginSecret);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreRespectsGlobalRotationPolicy(bool disableRotation)
    {
        var account = await SeedSession();
        var originalPreset = account.DeviceProfilePresetId;
        settings.DisableAllDeviceProfileRotation = disableRotation;
        var client = new LoginClientStub();
        using var cts = new CancellationTokenSource();
        var result = await new LoginWorkflowService(accounts, new NoWeGame(), client).ExecuteAsync(RestoreRequest(new LoginUI(), cts));
        Assert.Equal(disableRotation, result != null);
        Assert.Equal(disableRotation, originalPreset == account.DeviceProfilePresetId);
        Assert.Equal(disableRotation ? 1 : 0, client.RestoreCalls);
        Assert.True(account.IsDeviceProfileRotation);
        if (!disableRotation) Assert.Null(account.LoginSessionSecret);
    }

    [Theory]
    [InlineData("network", false)]
    [InlineData("cancel", false)]
    [InlineData("expired", true)]
    [InlineData("rejected", true)]
    public async Task TransientFailuresKeepCacheWhileRejectedSessionsClearIt(string failure, bool cleared)
    {
        var account = await SeedSession();
        var client = new LoginClientStub
        {
            RejectSession = failure == "rejected",
            RestoreFailure = failure switch
            {
                "network" => new IOException("transport failure"),
                "cancel" => new OperationCanceledException(),
                "expired" => new LoginException(-1, "expired"),
                _ => null
            }
        };
        using var cts = new CancellationTokenSource();
        var execute = () => new LoginWorkflowService(accounts, new NoWeGame(), client).ExecuteAsync(RestoreRequest(new LoginUI(), cts));
        if (failure == "rejected") Assert.Null(await execute());
        else await Assert.ThrowsAnyAsync<Exception>(execute);
        Assert.Equal(cleared, account.LoginSessionSecret == null);
        Assert.Equal("saved-quick-secret", account.SdoQuickLoginSecret);
    }

    [Theory]
    [InlineData("no-cache")]
    [InlineData("disabled")]
    [InlineData("hello")]
    [InlineData("cancelled")]
    public async Task IneligibleOrCancelledRestoreDoesNotSendRequests(string state)
    {
        var account = await SeedSession();
        if (state == "no-cache") account.LoginSessionSecret = null;
        if (state == "disabled") account.QuickLoginEnabled = false;
        if (state == "hello") typeof(AccountManager).GetProperty(nameof(AccountManager.CurrentCredType))!.SetValue(accounts, CredType.WindowsHello);
        using var cts = new CancellationTokenSource();
        if (state == "cancelled") cts.Cancel();
        var client = new LoginClientStub();
        var execute = () => new LoginWorkflowService(accounts, new NoWeGame(), client).ExecuteAsync(RestoreRequest(new LoginUI(), cts));
        if (state == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(execute);
        else Assert.Null(await execute());
        Assert.Equal(0, client.RestoreCalls);
    }

    [Fact]
    public async Task OptionalSessionEncryptionFailureDoesNotLoseQrAccountOrPreset()
    {
        await accounts.InitializeCredProviderAsync(CredType.NoEncryption);
        var provider = new SessionCacheFailure();
        typeof(AccountManager).GetProperty(nameof(AccountManager.CredProvider))!.SetValue(accounts, provider);
        var initialPresets = accounts.GetDeviceProfilePresets().Count;
        var result = await new LoginWorkflowService(accounts, new NoWeGame(), new LoginClientStub()).ExecuteAsync(Request(new LoginUI()));
        Assert.True(result!.IsAccountPersisted);
        var account = accounts.FindAccount("scanned", XIVAccountType.Sdo)!;
        Assert.Equal("new quick secret", account.SdoQuickLoginSecret);
        Assert.Null(account.LoginSessionSecret);
        Assert.NotNull(accounts.FindDeviceProfilePreset(account.DeviceProfilePresetId));
        Assert.Equal(initialPresets + 1, accounts.GetDeviceProfilePresets().Count);
        Assert.True(provider.CacheCleared);
    }

    private async Task<XIVAccount> SeedSession(bool weGame = false)
    {
        await accounts.InitializeCredProviderAsync(CredType.NoEncryption);
        settings.DisableAllDeviceProfileRotation = true;
        var account = SeedAccount("source");
        if (weGame)
        {
            accounts.RemoveAccount(account);
            account.AccountType = XIVAccountType.WeGame;
            account.WeGameLoginAccount = "source";
            account.GenerateID();
            accounts.AddAccount(account);
        }
        account.QuickLoginEnabled = true;
        account.SdoQuickLoginSecret = "saved-quick-secret";
        account.WeGameQuickLoginSecret = "saved-quick-secret";
        account.LoginSessionSecret = JsonSerializer.Serialize(new
        {
            AccountID = account.ID, SndaID = "source-id", TGT = "cached-tgt", GUID = "cached-guid",
            DeviceProfile = accounts.ResolveDeviceProfile(account).Snapshot
        });
        accounts.Save(account);
        return account;
    }

    private static LoginWorkflowRequest RestoreRequest(LoginUI ui, CancellationTokenSource cts, bool weGame = false) => new()
    {
        IsAutomaticLogin = true,
        LoginType = weGame ? LoginType.WeGame : LoginType.Slide,
        Username = "source", Password = string.Empty, QuickLoginEnabled = true, ForceWeGameTokenRecapture = false,
        Action = LoginAfterAction.Start, CurrentArea = Area, LoginAreas = [Area], LoginCancellationTokenSource = cts,
        LoginSessionRefreshSink = null, Interaction = ui, RequireDeviceProfileSetupForNewLogin = true,
        RequireDeviceProfileSetupForQRCodeLogin = true, DeviceProfileDebugEnabled = true
    };

    private sealed class SessionCacheFailure : ICredProvider
    {
        public bool CacheCleared { get; private set; }
        public string GetName() => "Session failure test";
        public string GetDescription() => GetName();
        public Task<bool> IsSupported() => Task.FromResult(true);
        public Task ClearCache() { CacheCleared = true; return Task.CompletedTask; }
        public Task Unregister() => Task.CompletedTask;
        public Task<string?> Encrypt(string? text) => Task.FromResult(text?.StartsWith('{') == true ? null : text);
        public Task<string?> Decrypt(string? text) => Task.FromResult(text);
    }
}
