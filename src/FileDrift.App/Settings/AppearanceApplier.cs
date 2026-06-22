using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FileDrift.App.Settings;

/// <summary>Applies a color scheme (Light / Dark / Pride) and accent to the running application.</summary>
public static class AppearanceApplier
{
    public const string Pride = "Pride";

    public static readonly (string Name, Color Color)[] Accents =
    [
        ("Default (blue)", Color.FromRgb(0x00, 0x67, 0xC0)),
        ("Teal",           Color.FromRgb(0x0F, 0x7B, 0x6C)),
        ("Purple",         Color.FromRgb(0x6F, 0x5B, 0xD7)),
        ("Magenta",        Color.FromRgb(0xC2, 0x39, 0xB3)),
        ("Orange",         Color.FromRgb(0xCA, 0x50, 0x10)),
        ("Green",          Color.FromRgb(0x10, 0x7C, 0x10)),
        ("Red",            Color.FromRgb(0xC4, 0x2B, 0x1C)),
    ];

    // Accent background brushes used by Primary buttons, toggles, selection, etc. Both the
    // SystemAccentColor* family (what ApplicationAccentColorManager drives) and the Fluent
    // AccentFillColor* family are overridden so the gradient reaches every accent surface.
    private static readonly string[] AccentBrushKeys =
    [
        "SystemAccentColorPrimaryBrush",
        "SystemAccentColorSecondaryBrush",
        "SystemAccentColorTertiaryBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
    ];

    public static void Apply(AppSettings settings)
    {
        bool pride = string.Equals(settings.ColorScheme, Pride, StringComparison.OrdinalIgnoreCase);
        bool dark = pride || string.Equals(settings.ColorScheme, "Dark", StringComparison.OrdinalIgnoreCase);
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);

        ClearPrideOverrides();
        if (pride)
            ApplyPride(theme);
        else
            ApplyAccent(settings.Accent, theme);
    }

    private static void ApplyAccent(string accentName, ApplicationTheme theme)
    {
        var color = ResolveAccent(accentName);
        ApplicationAccentColorManager.Apply(color, theme, systemGlassColor: false, systemAccentColor: false);
    }

    public static Color ResolveAccent(string accentName)
    {
        foreach (var (name, color) in Accents)
            if (string.Equals(name, accentName, StringComparison.OrdinalIgnoreCase))
                return color;
        return Accents[0].Color;
    }

    private static void ApplyPride(ApplicationTheme theme)
    {
        // Override the accent brushes directly with the rainbow gradient. We deliberately do NOT
        // call ApplicationAccentColorManager here — it would reset these keys to a solid color.
        var rainbow = BuildRainbowBrush();
        foreach (var key in AccentBrushKeys)
            Application.Current.Resources[key] = rainbow;
    }

    private static void ClearPrideOverrides()
    {
        foreach (var key in AccentBrushKeys)
            if (Application.Current.Resources.Contains(key))
                Application.Current.Resources.Remove(key);
    }

    private static LinearGradientBrush BuildRainbowBrush()
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(0xE4, 0x03, 0x03), 0.00), // red
                new(Color.FromRgb(0xFF, 0x8C, 0x00), 0.20), // orange
                new(Color.FromRgb(0xFF, 0xED, 0x00), 0.40), // yellow
                new(Color.FromRgb(0x00, 0x80, 0x26), 0.60), // green
                new(Color.FromRgb(0x00, 0x4C, 0xFF), 0.80), // blue
                new(Color.FromRgb(0x75, 0x07, 0x87), 1.00), // purple
            },
            new Point(0, 0), new Point(1, 0));
        brush.Freeze();
        return brush;
    }
}
