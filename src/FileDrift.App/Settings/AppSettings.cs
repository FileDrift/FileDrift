// SPDX-License-Identifier: GPL-3.0-or-later
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

    /// <summary>How often (seconds) the on-screen activity log refreshes during a run. Floor 0.5s; default 3s.
    /// The full per-line history still goes to the run-log file — this only throttles the live view.</summary>
    public double LogThrottleSeconds { get; set; } = 3.0;

    // ── window placement (restored next launch; position is clamped to the visible screen) ──
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }
}
