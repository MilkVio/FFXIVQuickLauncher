using System.Windows;
using System.Windows.Controls;
using XIVLauncher.Account;
using XIVLauncher.Login.Workflow;

namespace XIVLauncher.Windows;

public partial class QrLoginDeviceProfileChoiceWindow
{
    public QrLoginDeviceProfileSelection Selection { get; private set; } = QrLoginDeviceProfileSelection.Cancel;

    public QrLoginDeviceProfileChoiceWindow(IReadOnlyList<XIVAccount> independentDeviceProfileAccounts)
    {
        InitializeComponent();

        var accountOptions = independentDeviceProfileAccounts
                             .Select(account => new IndependentAccountOption(account))
                             .Prepend(IndependentAccountOption.NoSelection)
                             .ToArray();

        IndependentAccountComboBox.ItemsSource = accountOptions;
        IndependentAccountComboBox.SelectedIndex = 0;
        IndependentAccountComboBox.IsEnabled     = accountOptions.Length > 1;
        NoIndependentAccountTextBlock.Visibility = accountOptions.Length == 1
                                                       ? Visibility.Visible
                                                       : Visibility.Collapsed;

        RefreshChoiceState();
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IndependentAccountComboBox.SelectedItem is IndependentAccountOption { IsAccount: true } accountOption)
        {
            Selection = QrLoginDeviceProfileSelection.UseExistingIndependent(accountOption.AccountId!);
        }
        else
        {
            Selection = UseSharedRadioButton.IsChecked == true
                            ? QrLoginDeviceProfileSelection.UseShared
                            : QrLoginDeviceProfileSelection.CreateIndependent;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Selection    = QrLoginDeviceProfileSelection.Cancel;
        DialogResult = false;
    }

    private void IndependentAccountComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshChoiceState();

    private void ClearIndependentAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        IndependentAccountComboBox.SelectedIndex = 0;
        RefreshChoiceState();
    }

    private void RefreshChoiceState()
    {
        var hasSelectedIndependentAccount = IndependentAccountComboBox.SelectedItem is IndependentAccountOption { IsAccount: true };

        CreateIndependentRadioButton.IsEnabled      = !hasSelectedIndependentAccount;
        UseSharedRadioButton.IsEnabled              = !hasSelectedIndependentAccount;
        ClearIndependentAccountButton.IsEnabled     = hasSelectedIndependentAccount;
        IndependentAccountHintTextBlock.Opacity     = IndependentAccountComboBox.IsEnabled ? 1.0 : 0.56;
        IndependentAccountComboBox.Opacity          = IndependentAccountComboBox.IsEnabled ? 1.0 : 0.56;
    }

    private sealed class IndependentAccountOption
    {
        public static IndependentAccountOption NoSelection { get; } = new(null, "不选择");

        public IndependentAccountOption(XIVAccount account)
        {
            AccountId = account.ID;

            var accountType = account.IsWeGame ? "WeGame" : "盛趣";
            DisplayName = string.Equals(account.DisplayName, account.UserName, StringComparison.Ordinal)
                              ? $"{account.DisplayName} [{accountType}]"
                              : $"{account.DisplayName} ({account.UserName}, {accountType})";
        }

        private IndependentAccountOption(string? accountId, string displayName)
        {
            AccountId    = accountId;
            DisplayName  = displayName;
        }

        public string? AccountId { get; }

        public string DisplayName { get; }

        public bool IsAccount => !string.IsNullOrWhiteSpace(AccountId);
    }
}
