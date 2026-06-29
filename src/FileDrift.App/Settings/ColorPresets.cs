// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.App.Settings;

/// <summary>A named one-click appearance bundle: a base theme plus accent (buttons/highlights),
/// a title-bar tint, and a window background. Accent/TitleBar/Background may be a named color or a #hex.</summary>
public sealed record ColorPreset(string Name, string Theme, string Accent, string TitleBar, string Background);

public static class ColorPresets
{
    public const string Custom = "Custom";
    public const string Default = "Default";

    /// <summary>"Default" = follow the OS theme with the stock accent and the Mica chrome.</summary>
    public static readonly ColorPreset DefaultPreset = new(
        Default, AppearanceApplier.SystemTheme, "Default (blue)", AppearanceApplier.DefaultToken, AppearanceApplier.DefaultToken);

    public static readonly ColorPreset[] BuiltIn =
    [
        //          Name             Theme     Accent       TitleBar     Background
        new("Trans Rights", "Light", "#F5ABB9", "#5BCFFB", "#FFFFFF"),
    ];

    /// <summary>Resolves a preset name across the built-ins and the user's saved presets.</summary>
    public static ColorPreset? Find(string name) =>
        BuiltIn.Concat(PresetStore.Load())
               .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the name is reserved (the synthetic entries or a built-in) and can't be user-saved.</summary>
    public static bool IsReserved(string name) =>
        string.Equals(name, Custom, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, Default, StringComparison.OrdinalIgnoreCase) ||
        BuiltIn.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
