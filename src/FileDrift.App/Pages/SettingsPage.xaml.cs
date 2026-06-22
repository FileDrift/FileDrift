using System.Reflection;
using System.Windows.Controls;
using FileDrift.App.Settings;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Pages;

public partial class SettingsPage : Page
{
    private bool _initializing = true;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = SettingsStore.Load();

        ThemeBox.SelectedIndex = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        foreach (var (name, _) in AppearanceApplier.Accents)
            AccentBox.Items.Add(new ComboBoxItem { Content = name });
        Select(AccentBox, settings.Accent);

        TitleBarBox.Items.Add(new ComboBoxItem { Content = AppearanceApplier.DefaultTitleBar });
        foreach (var (name, _) in AppearanceApplier.Accents)
            TitleBarBox.Items.Add(new ComboBoxItem { Content = name });
        Select(TitleBarBox, settings.TitleBar);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;

        _initializing = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => ApplyAndSave();
    private void OnAccentChanged(object sender, SelectionChangedEventArgs e) => ApplyAndSave();
    private void OnTitleBarChanged(object sender, SelectionChangedEventArgs e) => ApplyAndSave();

    private void ApplyAndSave()
    {
        if (_initializing) return;

        var settings = new AppSettings
        {
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Light",
            Accent = (AccentBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Default (blue)",
            TitleBar = (TitleBarBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Default",
        };

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private static void Select(ComboBox box, string content)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && (string?)ci.Content == content) { box.SelectedItem = ci; return; }
        box.SelectedIndex = 0;
    }
}
