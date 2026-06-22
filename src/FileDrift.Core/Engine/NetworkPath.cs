namespace FileDrift.Core.Engine;

public static class NetworkPath
{
    public static bool IsUnc(string path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Reduces a UNC path to its share root: <c>\\server\share\a\b</c> → <c>\\server\share</c>.
    /// Returns the input unchanged if it is not a UNC path with at least server+share.</summary>
    public static string GetShareRoot(string uncPath)
    {
        if (!IsUnc(uncPath)) return uncPath;

        var parts = uncPath.TrimEnd('\\').Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : uncPath;
    }
}
