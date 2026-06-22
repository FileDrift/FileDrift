using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using FileDrift.Core.Persistence;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FileDrift.App.Pages;

public partial class SettingsPage : Page
{
    private bool _initializing = true;

    private static readonly (string Name, Color Color)[] Accents =
    [
        ("Default (blue)", Color.FromRgb(0x00, 0x67, 0xC0)),
        ("Teal",           Color.FromRgb(0x0F, 0x7B, 0x6C)),
        ("Purple",         Color.FromRgb(0x6F, 0x5B, 0xD7)),
        ("Magenta",        Color.FromRgb(0xC2, 0x39, 0xB3)),
        ("Orange",         Color.FromRgb(0xCA, 0x50, 0x10)),
        ("Green",          Color.FromRgb(0x10, 0x7C, 0x10)),
        ("Red",            Color.FromRgb(0xC4, 0x2B, 0x1C)),
    ];

    public SettingsPage()
    {
        InitializeComponent();

        ThemeBox.SelectedIndex = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? 1 : 0;

        foreach (var (name, color) in Accents)
            AccentBox.Items.Add(new ComboBoxItem { Content = name, Tag = color });
        AccentBox.SelectedIndex = 0;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;

        _initializing = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var theme = ThemeBox.SelectedIndex == 1 ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        ApplyAccent();
    }

    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        ApplyAccent();
    }

    private void ApplyAccent()
    {
        if (AccentBox.SelectedItem is not ComboBoxItem { Tag: Color color }) return;
        var theme = ApplicationThemeManager.GetAppTheme();
        ApplicationAccentColorManager.Apply(color, theme, systemGlassColor: false, systemAccentColor: false);
    }
}
