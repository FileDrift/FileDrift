// SPDX-License-Identifier: GPL-3.0-or-later
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

    /// <summary>File name of the MSAL token cache (held in <see cref="DataDirectory"/>).
    /// <para>This file contains OAuth refresh tokens — real secrets. On Windows MSAL encrypts it with
    /// DPAPI scoped to the current user, so it is not portable to another account or machine. Do not
    /// place the data directory in a synced or roaming location, and treat the file as a credential.</para></summary>
    public const string GraphTokenCacheFileName = "graph-token-cache.bin";

    /// <summary>%APPDATA%\FileDrift\graph-token-cache.bin</summary>
    public static string GraphTokenCache => Path.Combine(DataDirectory, GraphTokenCacheFileName);

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
