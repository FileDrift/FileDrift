using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FileDrift.App.Settings;

/// <summary>Applies the base theme, accent (highlight/button) color, and title-bar tint.</summary>
public static class AppearanceApplier
{
    public const string DefaultTitleBar = "Default";

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

    public static void Apply(AppSettings settings)
    {
        var theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        ApplicationAccentColorManager.Apply(ResolveAccent(settings.Accent), theme, false, false);
        ApplyTitleBar(settings);
    }

    /// <summary>Tints the running window's title bar. No-op if the main window isn't built yet
    /// (startup applies it again from <see cref="MainWindow"/> once the window loads).</summary>
    public static void ApplyTitleBar(AppSettings settings)
    {
        if (Application.Current?.MainWindow is not MainWindow main || main.AppTitleBar is not { } titleBar)
            return;

        if (string.IsNullOrEmpty(settings.TitleBar) ||
            string.Equals(settings.TitleBar, DefaultTitleBar, StringComparison.OrdinalIgnoreCase))
        {
            titleBar.ClearValue(System.Windows.Controls.Control.BackgroundProperty); // back to Mica
        }
        else
        {
            titleBar.Background = new SolidColorBrush(ResolveAccent(settings.TitleBar));
        }
    }

    public static Color ResolveAccent(string accentName)
    {
        foreach (var (name, color) in Accents)
            if (string.Equals(name, accentName, StringComparison.OrdinalIgnoreCase))
                return color;
        return Accents[0].Color;
    }
}
