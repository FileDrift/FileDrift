namespace FileDrift.Core.Persistence;

/// <summary>Resolves FileDrift's per-user data locations.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\FileDrift (created on access).</summary>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FileDrift");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>%APPDATA%\FileDrift\history.db</summary>
    public static string HistoryDatabase => Path.Combine(DataDirectory, "history.db");

    /// <summary>%APPDATA%\FileDrift\logs (created on access). Per-run activity logs.</summary>
    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
