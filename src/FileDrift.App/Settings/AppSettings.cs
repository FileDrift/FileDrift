namespace FileDrift.App.Settings;

/// <summary>User-facing preferences persisted to %APPDATA%\FileDrift\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Base theme: "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>Accent preset name (see <see cref="AppearanceApplier.Accents"/>). Drives buttons and highlights.</summary>
    public string Accent { get; set; } = "Default (blue)";

    /// <summary>Title bar tint: "Default" (Mica) or an accent preset name.</summary>
    public string TitleBar { get; set; } = "Default";
}
