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
    public const string SystemTheme = "System";

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
        ApplicationTheme theme;
        if (string.Equals(settings.Theme, SystemTheme, StringComparison.OrdinalIgnoreCase))
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: false); // resolves OS light/dark
            theme = ApplicationThemeManager.GetAppTheme();
            if (theme is ApplicationTheme.Unknown) theme = ApplicationTheme.Light;
        }
        else
        {
            theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        }

        ApplicationAccentColorManager.Apply(ResolveColor(settings.Accent), theme, false, false);
        ApplyWindowChrome(settings);
    }

    /// <summary>Tints the title bar and the background layer. No backdrop toggling — changing
    /// WindowBackdropType at runtime throws inside WPF-UI's FluentWindow.SetWindowChrome.
    /// A custom background simply paints an opaque layer over the Mica; Default stays transparent.</summary>
    public static void ApplyWindowChrome(AppSettings settings)
    {
        if (Application.Current?.MainWindow is not MainWindow main)
            return;

        if (main.AppTitleBar is { } titleBar)
        {
            if (IsDefault(settings.TitleBar))
                titleBar.ClearValue(Control.BackgroundProperty);
            else
                titleBar.Background = FrozenBrush(ResolveColor(settings.TitleBar));
        }

        if (main.BackgroundLayer is { } layer)
        {
            layer.Background = IsDefault(settings.Background)
                ? Brushes.Transparent
                : FrozenBrush(ResolveColor(settings.Background));
        }

        // In System mode (which only pairs with the default Mica background), follow live OS
        // light/dark changes; otherwise stop following so a manual theme choice sticks.
        bool followSystem = string.Equals(settings.Theme, SystemTheme, StringComparison.OrdinalIgnoreCase)
                            && IsDefault(settings.Background);
        if (followSystem)
            SystemThemeWatcher.Watch(main, WindowBackdropType.Mica, false);
        else
            SystemThemeWatcher.UnWatch(main);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
