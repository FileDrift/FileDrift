using System.IO;
using System.Text.Json;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Settings;

/// <summary>User-saved appearance presets, persisted to %APPDATA%\FileDrift\presets.json. Never throws.</summary>
public static class PresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "presets.json");

    public static List<ColorPreset> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<ColorPreset>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Adds or replaces a preset by name (case-insensitive) and persists the list.</summary>
    public static void Save(ColorPreset preset)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(preset);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions)); }
        catch { /* best-effort */ }
    }
}
