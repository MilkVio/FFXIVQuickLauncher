using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Windows;
using XIVLauncher.Windows.ViewModel;
using Xunit;

namespace XIVLauncher.Test.Login;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

[Collection("WPF UI")]
public sealed class DeviceProfileWindowTests
{
    [Fact]
    public void MergedWindowsLoadWithVioletResourcesAndDeviceBindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "VioletWindowTests", Guid.NewGuid().ToString("N"));
            Application? application = null;
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
            var startupContextProperty = typeof(App).GetProperty(nameof(App.StartupContext))!;
            var previousContext = startupContextProperty.GetValue(null);
            try
            {
                // Use only the application's resources. Never run App's startup/update/login handlers.
                System.Reflection.Assembly.SetEntryAssembly(null);
                Application.ResourceAssembly = typeof(App).Assembly;
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources = LoadVioletResources();
                using var accounts = new AccountManager(new LauncherSettingsV3(), directory);
                var settings = new LauncherSettingsV3
                {
                    RequireDeviceProfileSetupForQRCodeLogin = true,
                    DeviceProfileDebugEnabled = true,
                    DisableAllDeviceProfileRotation = true,
                    PatchPath = new DirectoryInfo(directory)
                };
                startupContextProperty.SetValue(null, new StartupContext { Settings = settings, AccountManager = accounts, Dispatcher = Dispatcher.CurrentDispatcher });

                var emptyChoice = new QrLoginDeviceProfileChoiceWindow([]);
                Assert.False(((ComboBox)emptyChoice.FindName("IndependentAccountComboBox")).IsEnabled);
                Render(emptyChoice, "qr-no-accounts.png", 480, 420);
                emptyChoice.Close();

                var account = new XIVAccount { SdoLoginAccount = "Test account", AccountType = XIVAccountType.Sdo };
                account.GenerateID();
                var choice = new QrLoginDeviceProfileChoiceWindow([account]);
                var options = (ComboBox)choice.FindName("IndependentAccountComboBox");
                var random = (RadioButton)choice.FindName("CreateIndependentRadioButton");
                var shared = (RadioButton)choice.FindName("UseSharedRadioButton");
                Assert.True(random.IsChecked);
                options.SelectedIndex = 1;
                Assert.False(random.IsEnabled);
                Assert.False(shared.IsEnabled);
                Render(choice, "qr-existing-account.png", 480, 420);
                options.SelectedIndex = 0;
                Assert.True(random.IsEnabled);
                Assert.True(shared.IsEnabled);
                choice.Close();

                var model = new SettingsWindowViewModel();
                var window = new SettingsWindow(model);
                ((ListBox)window.FindName("SidebarMenu")).SelectedIndex = 1;
                Render(window, "settings-device-options.png", 1040, 720);
                var settingNames = new[] { "RequireDeviceProfileSetupForQRCodeLogin", "DeviceProfileDebugEnabled", "DisableAllDeviceProfileRotation" };
                var switches = Descendants((DependencyObject)window.Content).OfType<ToggleButton>()
                    .Where(button => settingNames.Contains(BindingOperations.GetBinding(button, ToggleButton.IsCheckedProperty)?.Path.Path))
                    .ToDictionary(button => BindingOperations.GetBinding(button, ToggleButton.IsCheckedProperty)!.Path.Path);
                foreach (var name in settingNames)
                {
                    Assert.True(switches.TryGetValue(name, out var toggle), $"Missing setting binding: {name}");
                    Assert.True(toggle!.IsChecked);
                }
                switches["RequireDeviceProfileSetupForQRCodeLogin"].SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Assert.False(model.RequireDeviceProfileSetupForQRCodeLogin);
                Assert.True(model.DeviceProfileDebugEnabled);
                Assert.True(model.DisableAllDeviceProfileRotation);
                ((ScrollViewer)((TabItem)window.FindName("TabStart")).Content).ScrollToVerticalOffset(250);
                Render(window, "settings-device-options-scrolled.png", 1040, 720);
                window.Close();

                var profile = new AccountDeviceProfileWindow(accounts);
                Render(profile, "shared-device-editor.png", 1000, 740);
                profile.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                startupContextProperty.SetValue(null, previousContext);
                System.Reflection.Assembly.SetEntryAssembly(entryAssembly);
                application?.Shutdown();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF resource validation timed out");
        Assert.Null(failure);
    }

    private static ResourceDictionary LoadVioletResources()
    {
        System.Reflection.Assembly.Load("MaterialDesignThemes.Wpf");
        System.Reflection.Assembly.Load("MaterialDesignColors");
        System.Reflection.Assembly.Load("Dragablz");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UiResources", "App.xaml"));
        var dictionary = document.Root!.Element(wpf + "Application.Resources")!.Elements().Single();
        foreach (var attribute in document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            dictionary.SetAttributeValue(attribute.Name, attribute.Value);
        foreach (var source in dictionary.Descendants().Attributes("Source").Where(source => !source.Value.StartsWith("pack:", StringComparison.Ordinal)))
            source.Value = $"pack://application:,,,/XIVLauncherCN;component/{source.Value}";
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(Window window, string filename, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Assert.True(content.ActualWidth > 0);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(AppContext.BaseDirectory, "UiValidation");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, filename));
        encoder.Save(file);
    }
}
