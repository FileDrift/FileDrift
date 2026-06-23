using System.Reflection;
using System.Windows.Controls;
using FileDrift.App.Settings;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Pages;

public partial class SettingsPage : Page
{
    private bool _suppress = true;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = SettingsStore.Load();

        PresetBox.Items.Add(new ComboBoxItem { Content = ColorPresets.Custom });
        foreach (var preset in ColorPresets.All)
            PresetBox.Items.Add(new ComboBoxItem { Content = preset.Name });
        Select(PresetBox, settings.Preset);

        Select(ThemeBox, settings.Theme); // System / Light / Dark

        foreach (var (name, _) in AppearanceApplier.Accents)
            AccentBox.Items.Add(new ComboBoxItem { Content = name });
        Select(AccentBox, settings.Accent);

        TitleBarBox.Items.Add(new ComboBoxItem { Content = AppearanceApplier.DefaultToken });
        foreach (var (name, _) in AppearanceApplier.Accents)
            TitleBarBox.Items.Add(new ComboBoxItem { Content = name });
        Select(TitleBarBox, settings.TitleBar);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;

        _suppress = false;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;

        var name = SelectedText(PresetBox);
        if (name is null || string.Equals(name, ColorPresets.Custom, StringComparison.OrdinalIgnoreCase))
            return; // "Custom" is set implicitly by manual edits; selecting it changes nothing

        if (ColorPresets.Find(name) is not { } preset) return;

        var settings = new AppSettings
        {
            Preset = preset.Name,
            Theme = "Light",
            Accent = preset.Accent,
            TitleBar = preset.TitleBar,
            Background = preset.Background,
        };

        _suppress = true;
        Select(ThemeBox, "Light"); // presets are Light-based
        _suppress = false;

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private void OnManualChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;

        // Any manual edit drops to Custom and clears the preset's background tint.
        var settings = new AppSettings
        {
            Preset = ColorPresets.Custom,
            Theme = SelectedText(ThemeBox) ?? "Light",
            Accent = SelectedText(AccentBox) ?? "Default (blue)",
            TitleBar = SelectedText(TitleBarBox) ?? "Default",
            Background = "Default",
        };

        _suppress = true;
        Select(PresetBox, ColorPresets.Custom);
        _suppress = false;

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private static string? SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content as string;

    private static void Select(ComboBox box, string content)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && string.Equals((string?)ci.Content, content, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = ci; return; }
        box.SelectedIndex = 0;
    }
}
