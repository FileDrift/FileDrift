// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

/// <summary>The naming convention for FileDrift credentials in Windows Credential Manager.
/// One credential per share root, e.g. <c>FileDrift:\\server\share</c>.</summary>
public static class CredentialTarget
{
    public const string Prefix = "FileDrift:";

    /// <summary>The fallback credential used for any share without its own entry.</summary>
    public const string DefaultTarget = "FileDrift:(default)";

    /// <summary>The credential target name for the share root containing <paramref name="path"/>.</summary>
    public static string For(string path) => Prefix + NetworkPath.GetShareRoot(path);

    public static bool IsDefault(string targetName) =>
        string.Equals(targetName, DefaultTarget, StringComparison.Ordinal);

    /// <summary>Strips the prefix for display (returns the share root portion).</summary>
    public static string Display(string targetName) =>
        IsDefault(targetName)
            ? "(default)"
            : targetName.StartsWith(Prefix, StringComparison.Ordinal) ? targetName[Prefix.Length..] : targetName;
}
