using System.Windows;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml.Components;

namespace XIVLauncher.Windows;

public partial class ProxyManagerWindow : ChromeWindow
{
    public ProxyManagerWindow()
    {
        InitializeComponent();
        DataContext = new ProxyManagerWindowViewModel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
