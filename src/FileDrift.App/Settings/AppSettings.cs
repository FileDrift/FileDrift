namespace FileDrift.App.Settings;

/// <summary>User-facing preferences persisted to %APPDATA%\FileDrift\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Named preset ("Custom", "Ocean", …). Informational; the resolved colors below are authoritative.</summary>
    public string Preset { get; set; } = ColorPresets.Custom;

    /// <summary>Base theme: "System" (follow OS light/dark), "Light", or "Dark". Defaults to System.</summary>
    public string Theme { get; set; } = AppearanceApplier.SystemTheme;

    /// <summary>Accent: a named preset color or a #hex string. Drives buttons and highlights.</summary>
    public string Accent { get; set; } = "Default (blue)";

    /// <summary>Title bar tint: "Default" (Mica) or a named/#hex color.</summary>
    public string TitleBar { get; set; } = "Default";

    /// <summary>Window background: "Default" (Mica) or a #hex tint.</summary>
    public string Background { get; set; } = "Default";
}
