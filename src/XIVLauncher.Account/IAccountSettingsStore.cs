namespace XIVLauncher.Account;

public interface IAccountSettingsStore
{
    string CurrentAccountID { get; set; }

    /// <summary>
    ///     全局开关：为 true 时跳过所有账号的机器码轮换判定
    /// </summary>
    bool DisableAllDeviceProfileRotation { get; }
}
