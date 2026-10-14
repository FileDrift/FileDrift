// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.App;

/// <summary>App-wide shared service instances. Both are stateless per call (the repository opens a
/// pooled connection per operation; the credential store wraps Win32 calls), so one instance serves
/// every page — and the repository's schema-migration check runs once per launch instead of once per
/// page construction.</summary>
internal static class AppServices
{
    public static IRunRepository Repository { get; } = new SqliteRunRepository();

    public static ICredentialStore Credentials { get; } = new WindowsCredentialStore();

    /// <summary>A SmartFileEnumerator that can also reach SharePoint/OneDrive, mirroring what the CLI
    /// wires. The cloud enumerator is supplied as a FACTORY so it is only built when a cloud path is
    /// actually used -- a purely local run neither pays for Graph setup nor fails because Graph is
    /// unconfigured, which matters more in the GUI than the CLI since the app starts on this page.</summary>
    public static SmartFileEnumerator CreateEnumerator() => new(CreateCloudEnumerator);

    private static IFileEnumerator CreateCloudEnumerator()
    {
        var settings = GraphSettingsStore.Load();
        return new GraphFileEnumerator(new GraphClient(new GraphAuthenticator(settings), settings), settings);
    }

    /// <summary>Builds a write target for a cloud destination, or null for a local/SMB one (where the
    /// reconcile engine uses its own local target). Throws with an actionable message when a write
    /// scope has not been configured, rather than failing partway through a copy.</summary>
    public static async Task<IFileTarget?> CloudTargetAsync(string destPath, CancellationToken ct = default)
    {
        if (!CloudPath.IsSharePoint(destPath)) return null;

        var settings = GraphSettingsStore.Load();
        if (!settings.HasWriteScope)
            throw new InvalidOperationException(
                "Writing to SharePoint/OneDrive needs a write scope, but the Graph configuration " +
                $"requests only [{string.Join(", ", settings.Scopes ?? [])}]. Add Files.ReadWrite.All " +
                "to Scopes in graph.json and sign in again.");

        var client = new GraphClient(new GraphAuthenticator(settings), settings);
        var target = await GraphTargetResolver.ResolveAsync(client, destPath, ct).ConfigureAwait(false);
        return new GraphFileTarget(client, target, settings);
    }

    /// <summary>Whether Graph is configured and signed in, for surfacing cloud readiness in the UI.
    /// Never throws: this is asked in order to DISPLAY state, including the broken state.</summary>
    public static async Task<(bool Configured, string? SignedInAs, string? Problem)> CloudStatusAsync(
        CancellationToken ct = default)
    {
        try
        {
            var settings = GraphSettingsStore.Load();
            var issues = settings.Validate();
            if (issues.Count > 0) return (false, null, string.Join(" ", issues));

            var account = await new GraphAuthenticator(settings).GetSignedInAccountAsync(ct)
                .ConfigureAwait(false);
            return account is null
                ? (true, null, "Not signed in to Microsoft Graph.")
                : (true, account.Username, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
