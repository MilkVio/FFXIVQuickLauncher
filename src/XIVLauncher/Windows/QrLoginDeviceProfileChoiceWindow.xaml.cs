using System.Windows;
using XIVLauncher.Login;

namespace XIVLauncher.Windows;

public partial class QrLoginDeviceProfileChoiceWindow
{
    public NewAccountDeviceProfileChoice Choice { get; private set; } = NewAccountDeviceProfileChoice.Cancel;

    public QrLoginDeviceProfileChoiceWindow()
    {
        InitializeComponent();
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = UseSharedRadioButton.IsChecked == true
                     ? NewAccountDeviceProfileChoice.UseShared
                     : NewAccountDeviceProfileChoice.CreateIndependent;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice       = NewAccountDeviceProfileChoice.Cancel;
        DialogResult = false;
    }
}
