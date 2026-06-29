// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        PopulatePresetBox();
        Select(PresetBox, settings.Preset);

        Select(ThemeBox, settings.Theme); // System / Light / Dark

        foreach (var (name, _) in AppearanceApplier.Accents)
            AccentBox.Items.Add(new ComboBoxItem { Content = name });
        Select(AccentBox, settings.Accent);

        TitleBarBox.Items.Add(new ComboBoxItem { Content = AppearanceApplier.DefaultToken });
        foreach (var (name, _) in AppearanceApplier.Accents)
            TitleBarBox.Items.Add(new ComboBoxItem { Content = name });
        Select(TitleBarBox, settings.TitleBar);

        if (settings.Accent.StartsWith('#')) HexBox.Text = settings.Accent;
        UpdateHexSwatch(HexBox.Text);

        LogThrottleSlider.Value = settings.LogThrottleSeconds;
        LogThrottleValue.Text = $"{settings.LogThrottleSeconds:0.0} s";

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "unknown" : $"FileDrift {version.Major}.{version.Minor}.{version.Build}";
        DbPathText.Text = AppPaths.HistoryDatabase;

        _suppress = false;
        Loaded += OnSettingsLoaded;
    }

    /// <summary>Re-sync the throttle slider on each show in case it was changed on the Verify page.</summary>
    private void OnSettingsLoaded(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        LogThrottleSlider.Value = RuntimeOptions.LogThrottle.TotalSeconds;
        LogThrottleValue.Text = $"{RuntimeOptions.LogThrottle.TotalSeconds:0.0} s";
        _suppress = false;
    }

    private void PopulatePresetBox()
    {
        PresetBox.Items.Clear();
        PresetBox.Items.Add(new ComboBoxItem { Content = ColorPresets.Default });
        PresetBox.Items.Add(new ComboBoxItem { Content = ColorPresets.Custom });
        foreach (var p in ColorPresets.BuiltIn)
            PresetBox.Items.Add(new ComboBoxItem { Content = p.Name });
        foreach (var p in PresetStore.Load())
            PresetBox.Items.Add(new ComboBoxItem { Content = p.Name });
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;

        var name = SelectedText(PresetBox);
        if (name is null || string.Equals(name, ColorPresets.Custom, StringComparison.OrdinalIgnoreCase))
            return; // "Custom" is set implicitly by manual edits; selecting it changes nothing

        if (string.Equals(name, ColorPresets.Default, StringComparison.OrdinalIgnoreCase))
        {
            ApplyPreset(ColorPresets.DefaultPreset);
            return;
        }

        if (ColorPresets.Find(name) is { } preset)
            ApplyPreset(preset);
    }

    private void ApplyPreset(ColorPreset preset)
    {
        var settings = new AppSettings
        {
            Preset = preset.Name,
            Theme = preset.Theme,
            Accent = preset.Accent,
            TitleBar = preset.TitleBar,
            Background = preset.Background,
            LogThrottleSeconds = LogThrottleSlider.Value,
        };

        _suppress = true;
        Select(ThemeBox, preset.Theme);
        if (preset.Accent.StartsWith('#')) HexBox.Text = preset.Accent;
        else { HexBox.Text = ""; Select(AccentBox, preset.Accent); }
        UpdateHexSwatch(HexBox.Text);
        _suppress = false;

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private void OnManualChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ReferenceEquals(sender, AccentBox)) HexBox.Text = ""; // picking a named accent drops the hex override
        ApplyManual();
    }

    private void OnHexSet(object sender, RoutedEventArgs e)
    {
        if (!TryParseHex(HexBox.Text, out _)) return;
        ApplyManual();
    }

    /// <summary>Applies the current control state as a Custom appearance. A valid hex in the box wins
    /// over the named accent; the background tint is cleared (presets own backgrounds, not manual edits).</summary>
    private void ApplyManual()
    {
        var settings = new AppSettings
        {
            Preset = ColorPresets.Custom,
            Theme = SelectedText(ThemeBox) ?? "Light",
            Accent = CurrentAccent(),
            TitleBar = SelectedText(TitleBarBox) ?? "Default",
            Background = "Default",
            LogThrottleSeconds = LogThrottleSlider.Value,
        };

        _suppress = true;
        Select(PresetBox, ColorPresets.Custom);
        _suppress = false;

        AppearanceApplier.Apply(settings);
        SettingsStore.Save(settings);
    }

    private string CurrentAccent() =>
        TryParseHex(HexBox.Text, out _) ? HexBox.Text.Trim() : (SelectedText(AccentBox) ?? "Default (blue)");

    private void OnHexTextChanged(object sender, TextChangedEventArgs e) => UpdateHexSwatch(HexBox.Text);

    private void UpdateHexSwatch(string? value)
    {
        if (HexSwatch is null) return;
        if (TryParseHex(value, out var color))
        {
            HexSwatch.Background = new SolidColorBrush(color);
            if (HexSetButton is not null) HexSetButton.IsEnabled = true;
        }
        else
        {
            HexSwatch.ClearValue(Border.BackgroundProperty);
            if (HexSetButton is not null) HexSetButton.IsEnabled = false;
        }
    }

    private static bool TryParseHex(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || !value.TrimStart().StartsWith('#')) return false;
        try { color = (Color)ColorConverter.ConvertFromString(value.Trim())!; return true; }
        catch { return false; }
    }

    private async void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await Dialogs.InfoAsync("Save preset", "Enter a name for the preset.");
            return;
        }
        if (ColorPresets.IsReserved(name))
        {
            await Dialogs.InfoAsync("Save preset", $"\"{name}\" is reserved. Choose a different name.");
            return;
        }

        var current = SettingsStore.Load();
        PresetStore.Save(new ColorPreset(name, current.Theme, current.Accent, current.TitleBar, current.Background));

        _suppress = true;
        PopulatePresetBox();
        Select(PresetBox, name);
        _suppress = false;

        current.Preset = name;
        SettingsStore.Save(current);
        PresetNameBox.Text = "";
    }

    private void OnLogThrottleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var seconds = e.NewValue;
        if (LogThrottleValue is not null) LogThrottleValue.Text = $"{seconds:0.0} s";
        RuntimeOptions.SetLogThrottle(seconds); // live: an in-flight run picks this up on its next tick
        if (_suppress) return;

        var settings = SettingsStore.Load();
        settings.LogThrottleSeconds = seconds;
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
