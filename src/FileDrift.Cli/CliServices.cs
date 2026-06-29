// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.Cli;

/// <summary>Constructs Core services for CLI command handlers.</summary>
internal static class CliServices
{
    public static IRunRepository Repository() => new SqliteRunRepository();

    public static VerifyEngine Verify() => new(new SmartFileEnumerator(), Repository());

    public static ReconcileEngine Reconcile() => new();

    public static PreflightEngine Preflight() => new(new SmartFileEnumerator());

    public static ICredentialStore Credentials() => new WindowsCredentialStore();

    /// <summary>Resolves a saved credential by target name. Throws if the name was given but not found.</summary>
    public static NetworkCredential? ResolveCredential(string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        var cred = Credentials().GetCredential(targetName);
        return cred ?? throw new InvalidOperationException(
            $"No saved credential found for target '{targetName}'. Add it with 'FileDrift-CLI credential add' or in the app.");
    }

    /// <summary>Resolves the credential for a path. If <paramref name="explicitTarget"/> is given it is
    /// used (and must exist); otherwise auto-resolves the saved credential for the path's share root,
    /// falling back to the default credential — matching how the GUI applies saved credentials. Returns
    /// null when nothing is saved (e.g. a local path), so no flag is needed in the common case.</summary>
    public static NetworkCredential? ResolveCredentialForPath(string? explicitTarget, string path)
    {
        if (!string.IsNullOrWhiteSpace(explicitTarget))
            return ResolveCredential(explicitTarget);

        var store = Credentials();
        return store.GetCredential(CredentialTarget.For(path))
            ?? store.GetCredential(CredentialTarget.DefaultTarget);
    }
}
