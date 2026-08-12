// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using FileDrift.Core.Persistence;

namespace FileDrift.Core.Configuration;

/// <summary>Loads and saves <see cref="GraphSettings"/> as JSON, shared by the GUI and the CLI.
/// Follows the same conventions as the rest of FileDrift's persistence: an optional explicit path
/// (so it's testable without touching the real profile), and loads never throw.</summary>
public static class GraphSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Environment variables that override the file, for headless/CI use where writing to a
    /// user profile isn't appropriate. Overrides are read-only — <see cref="Save"/> never writes them.</summary>
    public const string ClientIdEnvVar = "FILEDRIFT_GRAPH_CLIENT_ID";
    public const string TenantIdEnvVar = "FILEDRIFT_GRAPH_TENANT_ID";
    public const string CloudEnvVar = "FILEDRIFT_GRAPH_CLOUD";

    /// <summary>%APPDATA%\FileDrift\graph.json</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "graph.json");

    /// <summary>Reads settings from disk, then applies any environment-variable overrides. Returns
    /// defaults (unconfigured) if the file is missing or unreadable — callers check
    /// <see cref="GraphSettings.IsConfigured"/> / <see cref="GraphSettings.Validate"/>.</summary>
    public static GraphSettings Load(string? path = null)
    {
        var settings = LoadFileOnly(path);

        // Environment wins over the file: a CI runner or scheduled task can point at a different
        // registration without rewriting the user's config.
        if (Environment.GetEnvironmentVariable(ClientIdEnvVar) is { Length: > 0 } clientId)
            settings.ClientId = clientId;
        if (Environment.GetEnvironmentVariable(TenantIdEnvVar) is { Length: > 0 } tenantId)
            settings.TenantId = tenantId;
        if (Environment.GetEnvironmentVariable(CloudEnvVar) is { Length: > 0 } cloud)
            settings.Cloud = cloud;

        return settings;
    }

    /// <summary>Reads only what's on disk, ignoring environment overrides. Use this before a
    /// read-modify-write: <see cref="Load"/> would fold an override into the object and then
    /// <see cref="Save"/> would bake that transient value permanently into the user's file.</summary>
    public static GraphSettings LoadFileOnly(string? path = null) => ReadFile(path ?? DefaultPath);

    private static GraphSettings ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new GraphSettings();
            return JsonSerializer.Deserialize<GraphSettings>(File.ReadAllText(path)) ?? new GraphSettings();
        }
        catch
        {
            // Malformed or unreadable config behaves as unconfigured rather than crashing a run;
            // Validate() then reports what's missing.
            return new GraphSettings();
        }
    }

    /// <summary>Writes settings to disk. Unlike <see cref="Load"/> this surfaces failures, because a
    /// silently-dropped config write would leave the user believing they'd saved credentials config.</summary>
    public static void Save(GraphSettings settings, string? path = null)
    {
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(target, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>Writes a fully-commented starter file if none exists, so the first run has something
    /// to edit rather than an empty path. Returns the path, and whether it was newly created.</summary>
    public static (string Path, bool Created) EnsureTemplate(string? path = null)
    {
        var target = path ?? DefaultPath;
        if (File.Exists(target)) return (target, false);

        Save(new GraphSettings(), target);
        return (target, true);
    }
}
