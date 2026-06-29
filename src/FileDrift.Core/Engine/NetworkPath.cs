// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

public static class NetworkPath
{
    public static bool IsUnc(string path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Reduces a UNC path to its share root: <c>\\server\share\a\b</c> → <c>\\server\share</c>.
    /// Returns the input unchanged for partial UNC input (e.g. <c>\\</c> or <c>\\server</c>) so it is
    /// safe to call on the in-progress text of a path box. Never throws on malformed input.</summary>
    public static string GetShareRoot(string uncPath)
    {
        if (!IsUnc(uncPath)) return uncPath;

        var trimmed = uncPath.TrimEnd('\\');
        if (trimmed.Length <= 2) return uncPath; // just "\\" — nothing to parse yet

        var parts = trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : uncPath;
    }
}
