using System.Reflection;
using System.Windows;
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

        SchemeBox.SelectedIndex = settings.ColorScheme switch
        {
            "Dark" => 1,
            "Pride" => 2,
            _ => 0,
        };

        foreach (var (name, _) in AppearanceApplier.Accents)
            AccentBox.Items.Add(new ComboBoxItem { Content = name });
        SelectAccent(settings.Accent);
        UpdateAccentAvailability();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;

        _initializing = false;
    }

    private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateAccentAvailability();
        ApplyAndSave();
    }

    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        ApplyAndSave();
    }

    private void UpdateAccentAvailability()
    {
        // Pride supplies its own colors, so the accent picker doesn't apply there.
        AccentCard.IsEnabled = (SchemeBox.SelectedItem as ComboBoxItem)?.Content as string != "Pride";
    }

    private void ApplyAndSave()
    {
        var settings = new AppSettings
        {
            ColorScheme = (SchemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Light",
            Accent = (AccentBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Default (blue)",
        };

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private void SelectAccent(string name)
    {
        foreach (var item in AccentBox.Items)
            if (item is ComboBoxItem ci && (string?)ci.Content == name) { AccentBox.SelectedItem = ci; return; }
        AccentBox.SelectedIndex = 0;
    }
}
