using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.Workflow;

namespace XIVLauncher.Login.Client;

public interface ILoginClient
{
    Task<LoginResult> LoginAsync(LoginType loginType, LoginRequest request, CancellationToken cancellationToken = default);

    Task<LoginResult> LoginBySessionKey(
        string account,
        string autoLoginSessionKey,
        ILoginSessionRefreshSink? loginSessionRefreshSink,
        DeviceProfileSnapshot deviceProfile);

    Task<LoginResult> LoginWithFallback(
        LoginType loginType,
        LoginType fallbackLoginType,
        Func<LoginType, LoginRequest> requestFactory,
        CancellationToken cancellationToken = default);
}
