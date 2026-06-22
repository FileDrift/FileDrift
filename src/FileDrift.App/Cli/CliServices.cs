using System.Net;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Cli;

/// <summary>Constructs Core services for CLI command handlers.</summary>
internal static class CliServices
{
    public static IRunRepository Repository() => new SqliteRunRepository();

    public static VerifyEngine Verify() => new(new SmartFileEnumerator(), Repository());

    public static PreflightEngine Preflight() => new(new SmartFileEnumerator());

    public static ICredentialStore Credentials() => new WindowsCredentialStore();

    /// <summary>Resolves a saved credential by target name. Throws if the name was given but not found.</summary>
    public static NetworkCredential? ResolveCredential(string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        var cred = Credentials().GetCredential(targetName);
        return cred ?? throw new InvalidOperationException(
            $"No saved credential found for target '{targetName}'. Add it in the app or with Credential Manager.");
    }
}
