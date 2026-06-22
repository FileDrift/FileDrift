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
        new("Pride",          "#750787", "#E40303", "#F4DCEA"),
        new("African Unity",  "#1B7A34", "#C1121F", "#FBF6E6"),
        new("Sunset",         "#E2632A", "#8A2E5D", "#FFF3EA"),
        new("Desert",         "#BE7A3F", "#6E5A34", "#FAF4E8"),
        new("Ocean",          "#0E7C7B", "#143C6B", "#EDF5F8"),
        new("Slate",          "#51657A", "#313B45", "#F1F3F6"),
    ];

    public static ColorPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
