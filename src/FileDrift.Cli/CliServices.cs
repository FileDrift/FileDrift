// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.Cli;

/// <summary>Constructs Core services for CLI command handlers.</summary>
internal static class CliServices
{
    public static IRunRepository Repository() => new SqliteRunRepository();

    public static VerifyEngine Verify() => new(Enumerator(), Repository());

    public static ReconcileEngine Reconcile() => new();

    public static PreflightEngine Preflight() => new(Enumerator());

    /// <summary>A SmartFileEnumerator that can also reach SharePoint/OneDrive. The cloud enumerator is
    /// supplied as a factory so it is only built when a cloud path is actually used -- a local run
    /// neither pays for Graph setup nor fails because Graph is unconfigured.</summary>
    private static SmartFileEnumerator Enumerator() => new(CreateCloudEnumerator);

    /// <summary>Builds a write target for a cloud destination, or null when the destination is a
    /// local or SMB path (in which case ReconcileEngine uses its own local target).</summary>
    public static async Task<IFileTarget?> CloudTargetAsync(string destPath, CancellationToken ct = default)
    {
        if (!CloudPath.IsSharePoint(destPath)) return null;

        var settings = GraphSettingsStore.Load();
        if (!settings.HasWriteScope)
            throw new InvalidOperationException(
                "Writing to SharePoint/OneDrive needs a write scope, but graph.json requests only " +
                $"[{string.Join(", ", settings.Scopes ?? [])}]. Add it and sign in again:" + Environment.NewLine +
                "  FileDrift-CLI graph set --scopes Files.ReadWrite.All,Sites.Read.All" + Environment.NewLine +
                "  FileDrift-CLI graph login" + Environment.NewLine +
                "The app registration must also have that delegated permission consented.");

        var client = new GraphClient(new GraphAuthenticator(settings), settings);
        var target = await GraphTargetResolver.ResolveAsync(client, destPath, ct).ConfigureAwait(false);
        return new GraphFileTarget(client, target, settings);
    }

    private static IFileEnumerator CreateCloudEnumerator()
    {
        var settings = GraphSettingsStore.Load();
        // The GraphClient owns an HttpClient; it lives as long as the enumerator, which lives as long
        // as the run, so it is not disposed here. A CLI process is short-lived and exits after one run.
        return new GraphFileEnumerator(new GraphClient(new GraphAuthenticator(settings), settings), settings);
    }

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
