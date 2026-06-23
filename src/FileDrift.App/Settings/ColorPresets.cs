namespace FileDrift.App.Settings;

/// <summary>A named one-click appearance bundle. All presets are Light-based; each sets an accent
/// (buttons/highlights), a title-bar tint, and a soft window background.</summary>
public sealed record ColorPreset(string Name, string Accent, string TitleBar, string Background);

public static class ColorPresets
{
    public const string Custom = "Custom";

    public static readonly ColorPreset[] All =
    [
        //                Name              Accent       TitleBar     Background (soft light tint)
        new("Trans Rights",   "#F5ABB9", "#5BCFFB", "#FFFFFF"),
    ];

    public static ColorPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
