using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.Common.Http;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel;

public sealed partial class ProxyManagerWindowViewModel : ObservableObject
{
    private static readonly TimeSpan TestCooldown = TimeSpan.FromSeconds(3);

    private readonly IDialogService _dialogService;
    private readonly Func<LoginProxyEntry, Task<ProxyTestResult>> testEntryAsync;
    private bool suppressSave;
    private bool isTestingAll;

    public ObservableCollection<ProxyEntryRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial bool ProxyEnabled { get; set; }

    partial void OnProxyEnabledChanged(bool value)
    {
        if (!suppressSave)
            SaveSettings(s => s.LoginProxyEnabled = value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualMode))]
    public partial int PickModeIndex { get; set; }

    partial void OnPickModeIndexChanged(int value)
    {
        if (!suppressSave && Enum.IsDefined(typeof(LoginProxyPickMode), value))
            SaveSettings(s => s.LoginProxyPickMode = (LoginProxyPickMode)value);
    }

    public bool IsManualMode => PickModeIndex == (int)LoginProxyPickMode.Manual;

    internal ProxyManagerWindowViewModel
    (
        IDialogService? dialogService = null,
        Func<LoginProxyEntry, Task<ProxyTestResult>>? testEntryAsync = null
    )
    {
        _dialogService = dialogService ?? new DialogService();
        this.testEntryAsync = testEntryAsync ?? (entry => LoginProxyPool.TestEntryAsync(entry, LoginProxySetup.TestUrl));
        ReloadFromSettings();
    }

    public void ReloadFromSettings()
    {
        suppressSave = true;

        try
        {
            ProxyEnabled  = App.Settings.LoginProxyEnabled;
            PickModeIndex = (int)App.Settings.LoginProxyPickMode;
            RebuildRows();
        }
        finally
        {
            suppressSave = false;
        }
    }

    [RelayCommand]
    private void AddEntry()
    {
        var result = _dialogService.ShowProxyEntryEdit();
        if (result == null)
            return;

        SaveSettings(s => s.LoginProxyEntries = [..s.LoginProxyEntries, result]);
        RebuildRows();
    }

    [RelayCommand]
    private void EditEntry(ProxyEntryRowViewModel? row)
    {
        if (row == null)
            return;

        var result = _dialogService.ShowProxyEntryEdit(row.Entry);
        if (result == null)
            return;

        result.Id = row.Entry.Id;
        SaveSettings(s => s.LoginProxyEntries = s.LoginProxyEntries.Select(e => e.Id == result.Id ? result : e).ToList());
        RebuildRows();
    }

    [RelayCommand]
    private void RemoveEntry(ProxyEntryRowViewModel? row)
    {
        if (row == null)
            return;

        SaveSettings
        (s =>
            {
                s.LoginProxyEntries = s.LoginProxyEntries.Where(e => e.Id != row.Entry.Id).ToList();
                if (s.LoginProxyManualEntryId == row.Entry.Id)
                    s.LoginProxyManualEntryId = null;
            }
        );
        RebuildRows();
    }

    [RelayCommand]
    private Task TestEntry(ProxyEntryRowViewModel? row) =>
        row == null ? Task.CompletedTask : TestEntryCoreAsync(row);

    [RelayCommand]
    private async Task TestAll()
    {
        if (isTestingAll)
            return;

        isTestingAll = true;

        try
        {
            foreach (var row in Entries.ToArray())
                await TestEntryCoreAsync(row, true);
        }
        finally
        {
            isTestingAll = false;
        }
    }

    private async Task TestEntryCoreAsync(ProxyEntryRowViewModel row, bool ignoreCooldown = false)
    {
        if (row.IsTesting)
            return;

        if (!ignoreCooldown && DateTimeOffset.UtcNow - row.LastTestUtc < TestCooldown)
            return;

        row.LastTestUtc = DateTimeOffset.UtcNow;
        row.IsTesting   = true;
        row.LatencyText = "测试中…";

        try
        {
            var result = await testEntryAsync(row.Entry);
            row.LatencyText = result.Ok ? $"{result.LatencyMs} ms" : $"失败: {TrimError(result.Error)}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxyManager] 代理测速异常");
            row.LatencyText = $"失败: {TrimError(ex.Message)}";
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    private void OnRowEnabledChanged(ProxyEntryRowViewModel row, bool enabled) =>
        SaveSettings
        (s =>
            s.LoginProxyEntries = s.LoginProxyEntries
                                   .Select
                                    (
                                        e =>
                                        {
                                            if (e.Id == row.Entry.Id)
                                                e.Enabled = enabled;

                                            return e;
                                        }
                                    )
                                   .ToList()
        );

    private void OnRowDesignatedChanged(ProxyEntryRowViewModel row, bool designated)
    {
        if (designated)
        {
            foreach (var other in Entries.Where(e => e != row))
                other.SetDesignatedSilently(false);

            SaveSettings(s => s.LoginProxyManualEntryId = row.Entry.Id);
        }
        else if (App.Settings.LoginProxyManualEntryId == row.Entry.Id)
        {
            SaveSettings(s => s.LoginProxyManualEntryId = null);
        }
    }

    private void RebuildRows()
    {
        Entries.Clear();

        var manualId = App.Settings.LoginProxyManualEntryId;
        foreach (var entry in App.Settings.LoginProxyEntries)
            Entries.Add(new ProxyEntryRowViewModel(entry, manualId == entry.Id, OnRowEnabledChanged, OnRowDesignatedChanged));
    }

    private static void SaveSettings(Action<LauncherSettingsV3> updater)
    {
        App.Settings.Update(updater);
        LoginProxySetup.ApplyFromSettings();
    }

    private static string TrimError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "未知错误";

        var firstLine = error.Split('\n')[0].Trim();
        return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
    }
}

public sealed partial class ProxyEntryRowViewModel : ObservableObject
{
    private readonly Action<ProxyEntryRowViewModel, bool> onEnabledChanged;
    private readonly Action<ProxyEntryRowViewModel, bool> onDesignatedChanged;
    private          bool                                 suppressEvents;

    internal LoginProxyEntry Entry { get; }

    public string Name      { get; }
    public string MaskedUrl { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (!suppressEvents)
            onEnabledChanged(this, value);
    }

    [ObservableProperty]
    public partial bool IsDesignated { get; set; }

    partial void OnIsDesignatedChanged(bool value)
    {
        if (!suppressEvents)
            onDesignatedChanged(this, value);
    }

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "未测试";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotTesting))]
    public partial bool IsTesting { get; set; }

    public bool IsNotTesting => !IsTesting;

    public DateTimeOffset LastTestUtc { get; set; } = DateTimeOffset.MinValue;

    public ProxyEntryRowViewModel
    (
        LoginProxyEntry                       entry,
        bool                                  isDesignated,
        Action<ProxyEntryRowViewModel, bool>  onEnabledChanged,
        Action<ProxyEntryRowViewModel, bool>  onDesignatedChanged
    )
    {
        Entry                    = entry;
        this.onEnabledChanged    = onEnabledChanged;
        this.onDesignatedChanged = onDesignatedChanged;

        Name      = GetDisplayName(entry);
        MaskedUrl = entry.GetMaskedUrl();

        suppressEvents = true;
        Enabled        = entry.Enabled;
        IsDesignated   = isDesignated;
        suppressEvents = false;
    }

    public void SetDesignatedSilently(bool value)
    {
        suppressEvents = true;
        IsDesignated   = value;
        suppressEvents = false;
    }

    private static string GetDisplayName(LoginProxyEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name))
            return entry.Name;

        return Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)
                   ? $"{uri.Host}:{uri.Port}"
                   : entry.Url;
    }
}
