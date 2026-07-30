namespace XIVLauncher.Login.Workflow;

public sealed record QrLoginDeviceProfileSelection
(
    NewAccountDeviceProfileChoice Choice,
    string?                       AccountId = null
)
{
    public static QrLoginDeviceProfileSelection Cancel =>
        new(NewAccountDeviceProfileChoice.Cancel);

    public static QrLoginDeviceProfileSelection UseShared =>
        new(NewAccountDeviceProfileChoice.UseShared);

    public static QrLoginDeviceProfileSelection CreateIndependent =>
        new(NewAccountDeviceProfileChoice.CreateIndependent);

    public static QrLoginDeviceProfileSelection UseExistingIndependent(string accountId) =>
        new(NewAccountDeviceProfileChoice.UseExistingIndependent, accountId);
}
