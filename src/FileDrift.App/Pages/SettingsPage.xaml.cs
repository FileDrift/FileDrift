// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileDrift.App.Settings;
using FileDrift.Core;
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

        VersionText.Text = $"FileDrift {AppInfo.Version}";
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

        // Fire-and-forget: this reaches out to the token cache and must never delay showing the page.
        _ = RefreshCloudStatusAsync();
    }

    /// <summary>Shows whether Graph is configured and signed in. Never throws: it is asked in order to
    /// DISPLAY state, including the broken state, so a configuration problem must appear as text here
    /// rather than as an exception.</summary>
    private async Task RefreshCloudStatusAsync()
    {
        CloudSignInButton.IsEnabled = false;
        CloudSignOutButton.IsEnabled = false;

        var (configured, signedInAs, problem) = await AppServices.CloudStatusAsync();

        if (!configured)
        {
            CloudStatusText.Text = "Not configured.";
            // Point at the file rather than describing it: the operator has to edit it.
            CloudHintText.Text = $"{problem} Set ClientId and TenantId in {AppServices.GraphConfigPath}, " +
                                 "or run \"FileDrift-CLI graph set\".";
            CloudHintText.Visibility = Visibility.Visible;
            CloudSignInButton.IsEnabled = false;
            CloudSignOutButton.IsEnabled = false;
            return;
        }

        if (signedInAs is null)
        {
            CloudStatusText.Text = "Configured, not signed in.";
            CloudHintText.Text = "Verify and reconcile against a SharePoint path will fail until you sign in. " +
                                 "Signing in here caches a token so runs never prompt.";
            CloudHintText.Visibility = Visibility.Visible;
            CloudSignInButton.IsEnabled = true;
            CloudSignOutButton.IsEnabled = false;
            return;
        }

        CloudStatusText.Text = $"Signed in as {signedInAs}";
        // State the consequence of delegated access plainly: it bounds what any cloud run can see.
        CloudHintText.Text = "FileDrift acts as this account, so a comparison covers only what it can " +
                             "see. Certificates record which account read the data.";
        CloudHintText.Visibility = Visibility.Visible;
        CloudSignInButton.IsEnabled = true;   // allows switching account
        CloudSignOutButton.IsEnabled = true;
    }

    private async void OnCloudSignIn(object sender, RoutedEventArgs e)
    {
        CloudSignInButton.IsEnabled = false;
        CloudStatusText.Text = "Signing in…";
        try
        {
            // Device-code instructions have nowhere to go in a GUI but a dialog; without this the
            // operator would be left staring at a window that appears to do nothing.
            var account = await AppServices.CloudSignInAsync(
                deviceCodePrompt: msg => Dispatcher.Invoke(() =>
                    _ = Dialogs.InfoAsync("Sign in on another device", msg)));

            await Dialogs.InfoAsync("Signed in", $"Signed in to Microsoft Graph as {account.Username}.");
        }
        catch (Exception ex)
        {
            // The authenticator already translates Entra failures into the actual fix, so show its
            // message rather than wrapping it in something vaguer.
            await Dialogs.InfoAsync("Sign-in failed", ex.Message);
        }
        finally
        {
            await RefreshCloudStatusAsync();
        }
    }

    private async void OnCloudSignOut(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync("Sign out of Microsoft Graph",
                "Remove the cached sign-in from this machine? Verify and reconcile against a " +
                "SharePoint path will fail until you sign in again.",
                confirmText: "Sign out"))
            return;

        try
        {
            var removed = await AppServices.CloudSignOutAsync();
            await Dialogs.InfoAsync("Signed out",
                removed == 0 ? "There was no cached sign-in to remove." : "Cached sign-in removed.");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Sign-out failed", ex.Message);
        }
        finally
        {
            await RefreshCloudStatusAsync();
        }
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

        ThrottleSettingSaver.RequestSave(); // debounced: a drag fires this on every snap point
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
