namespace FileDrift.Core.Engine;

/// <summary>The naming convention for FileDrift credentials in Windows Credential Manager.
/// One credential per share root, e.g. <c>FileDrift:\\server\share</c>.</summary>
public static class CredentialTarget
{
    public const string Prefix = "FileDrift:";

    /// <summary>The credential target name for the share root containing <paramref name="path"/>.</summary>
    public static string For(string path) => Prefix + NetworkPath.GetShareRoot(path);

    /// <summary>Strips the prefix for display (returns the share root portion).</summary>
    public static string Display(string targetName) =>
        targetName.StartsWith(Prefix, StringComparison.Ordinal) ? targetName[Prefix.Length..] : targetName;
}
