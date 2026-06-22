using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Persistence;
using Wpf.Ui.Appearance;

namespace FileDrift.App.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        ThemeSwitch.IsChecked = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        var theme = ThemeSwitch.IsChecked == true ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme);
    }
}
