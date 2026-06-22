using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FileDrift.App.Settings;

/// <summary>Applies the base theme, accent color, title-bar tint, and window background.</summary>
public static class AppearanceApplier
{
    public const string DefaultToken = "Default";
    private const string BackgroundBrushKey = "ApplicationBackgroundBrush";

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
        ApplicationAccentColorManager.Apply(ResolveColor(settings.Accent), theme, false, false);
        ApplyWindowChrome(settings);
    }

    /// <summary>Applies the title-bar tint and window background. The background's app-level brush
    /// override is set unconditionally; the window-specific parts run only once the window exists.</summary>
    public static void ApplyWindowChrome(AppSettings settings)
    {
        bool customBackground = !IsDefault(settings.Background);

        // App-level background brush override (independent of the window instance).
        if (customBackground)
            Application.Current.Resources[BackgroundBrushKey] = new SolidColorBrush(ResolveColor(settings.Background));
        else if (Application.Current.Resources.Contains(BackgroundBrushKey))
            Application.Current.Resources.Remove(BackgroundBrushKey);

        if (Application.Current?.MainWindow is not MainWindow main)
            return;

        // Title bar.
        if (main.AppTitleBar is { } titleBar)
        {
            if (IsDefault(settings.TitleBar))
                titleBar.ClearValue(Control.BackgroundProperty);
            else
                titleBar.Background = new SolidColorBrush(ResolveColor(settings.TitleBar));
        }

        // Window background: a solid tint requires turning the Mica backdrop off.
        if (customBackground)
        {
            main.WindowBackdropType = WindowBackdropType.None;
            main.Background = new SolidColorBrush(ResolveColor(settings.Background));
        }
        else
        {
            main.WindowBackdropType = WindowBackdropType.Mica;
            main.ClearValue(Window.BackgroundProperty);
        }
    }

    /// <summary>Resolves a "#hex" string or a named accent to a Color (falls back to the default blue).</summary>
    public static Color ResolveColor(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.StartsWith('#'))
        {
            try { return (Color)ColorConverter.ConvertFromString(value)!; }
            catch { /* fall through to named lookup */ }
        }

        foreach (var (name, color) in Accents)
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return color;

        return Accents[0].Color;
    }

    private static bool IsDefault(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, DefaultToken, StringComparison.OrdinalIgnoreCase);
}
