namespace FileDrift.App.Settings;

/// <summary>User-facing preferences persisted to %APPDATA%\FileDrift\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>"Light", "Dark", or "Pride".</summary>
    public string ColorScheme { get; set; } = "Light";

    /// <summary>Accent preset name (see <see cref="AppearanceApplier.Accents"/>). Ignored for "Pride".</summary>
    public string Accent { get; set; } = "Default (blue)";
}
