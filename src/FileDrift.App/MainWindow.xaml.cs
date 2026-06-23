using FileDrift.App.Settings;
using Wpf.Ui.Controls;

namespace FileDrift.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Cache the Verify page so a running verify survives navigation to Settings/History and back.
        RootNavigation.SetPageProviderService(new PageProvider());
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(Pages.VerifyPage));
            AppearanceApplier.ApplyWindowChrome(SettingsStore.Load()); // window now exists
        };
    }
}
