using System.Windows;
using XIVLauncher.Common.Http;
using XIVLauncher.Xaml.Components;

namespace XIVLauncher.Windows;

public partial class ProxyEntryEditWindow : ChromeWindow
{
    public LoginProxyEntry? Result { get; private set; }

    public ProxyEntryEditWindow(LoginProxyEntry? entry = null)
    {
        InitializeComponent();

        if (entry == null)
            return;

        Title                     = "编辑代理条目";
        NameTextBox.Text          = entry.Name;
        UrlTextBox.Text           = entry.Url;
        EnabledCheckBox.IsChecked = entry.Enabled;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlTextBox.Text.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp                    ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            ErrorTextBlock.Text       = "地址格式无效，仅支持 http:// 开头的代理地址";
            ErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
            name = $"{uri.Host}:{uri.Port}";

        Result = new LoginProxyEntry
        {
            Name    = name,
            Url     = url,
            Enabled = EnabledCheckBox.IsChecked == true
        };
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
