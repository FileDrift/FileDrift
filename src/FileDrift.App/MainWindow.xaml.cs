using FileDrift.App.Settings;
using Wpf.Ui.Controls;

namespace FileDrift.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(Pages.VerifyPage));
            AppearanceApplier.ApplyTitleBar(SettingsStore.Load()); // window now exists
        };
    }
}
