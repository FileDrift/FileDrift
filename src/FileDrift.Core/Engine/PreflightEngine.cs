// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using FileDrift.Core.Cloud;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Checks that source and destination are reachable and reports file/byte counts
/// before a full verify is run.</summary>
public sealed class PreflightEngine
{
    private readonly IFileEnumerator _enumerator;

    public PreflightEngine(IFileEnumerator enumerator) => _enumerator = enumerator;

    public async Task<PreFlightResult> RunAsync(
        string sourcePath,
        string destPath,
        VerifyOptions options,
        NetworkCredential? sourceCredential = null,
        NetworkCredential? destCredential = null,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IFileTarget? destinationTarget = null,
        bool checkWrite = false)
    {
        // Same fail-fast as VerifyEngine: a cloud path here would otherwise reach Directory.Exists
        // and report a misleading "path not found".
        CloudPath.GuardSupported(sourcePath, destPath, options);

        var issues = new List<string>();
        var connections = new List<IDisposable>();

        // Source files are assessed against the DESTINATION rules, so this only applies when the
        // destination is a cloud target. A cloud source going to a local destination has no such
        // restrictions -- Windows accepts everything SharePoint does and more.
        var migration = CloudPath.IsSharePoint(destPath)
            ? new CloudMigrationPolicy(destPath, CloudLimits)
            : null;
        CloudMigrationReport? migrationReport = migration is null ? null : new();

        bool sourceOk = false, destOk = false;
        long? sourceCount = null, sourceBytes = null, destCount = null, destBytes = null;

        try
        {
            TryConnect(connections, sourcePath, sourceCredential, "source", issues);
            TryConnect(connections, destPath, destCredential, "destination", issues);

            (sourceOk, sourceCount, sourceBytes) =
                await ProbeAsync(sourcePath, options, VerifyPhase.EnumeratingSource, "source", issues, progress,
                    cancellationToken, migration, migrationReport);
            (destOk, destCount, destBytes) =
                await ProbeAsync(destPath, options, VerifyPhase.EnumeratingDestination, "destination", issues, progress, cancellationToken);
        }
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }

        // Deliberately after the read checks: there is no point writing to a destination that could
        // not be enumerated, and a failure there is the more useful thing to report first.
        var writeProbe = checkWrite && destOk
            ? await ProbeWriteAsync(destinationTarget ?? new LocalFileTarget(destPath), issues, cancellationToken)
            : WriteProbeResult.NotAttempted();

        // Silence here would read as "write access is fine". Say plainly that it was not tested.
        if (!checkWrite && destOk)
            issues.Add(
                "[WARN] Write access to the destination was NOT tested. Reported permissions can differ from what a write actually does (an inherited deny, a retention or hold policy, a library requiring check-out, a quota at its limit). Pass --check-write to test it with a tiny temporary file before relying on this.");

        if (migrationReport is { HasFindings: true })
            AddMigrationIssues(issues, migrationReport);

        progress?.Report(new VerifyProgress { Phase = VerifyPhase.Done, Message = "Preflight complete" });

        return new PreFlightResult
        {
            RunId = Guid.NewGuid(),
            CheckedAtUtc = DateTime.UtcNow,
            SourcePath = sourcePath,
            DestPath = destPath,
            SourceAccessible = sourceOk,
            DestAccessible = destOk,
            SourceFileCount = sourceCount,
            DestFileCount = destCount,
            SourceTotalBytes = sourceBytes,
            DestTotalBytes = destBytes,
            Issues = issues,
            Migration = migrationReport,
            WriteProbe = writeProbe,
        };
    }

    private static void TryConnect(
        List<IDisposable> connections, string path, NetworkCredential? credential, string label, List<string> issues)
    {
        if (credential is null || !NetworkPath.IsUnc(path)) return;
        try
        {
            connections.Add(new NetworkConnection(NetworkPath.GetShareRoot(path), credential));
        }
        catch (Exception ex)
        {
            issues.Add($"[ERROR] Could not authenticate to {label} ({NetworkPath.GetShareRoot(path)}): {ex.Message}");
        }
    }

    private async Task<(bool ok, long? count, long? bytes)> ProbeAsync(
        string path,
        VerifyOptions options,
        VerifyPhase phase,
        string label,
        List<string> issues,
        IProgress<VerifyProgress>? progress,
        CancellationToken ct,
        CloudMigrationPolicy? migration = null,
        CloudMigrationReport? migrationReport = null)
    {
        // Directory.Exists is meaningless for a cloud target -- a SharePoint URL is not a filesystem
        // path -- and would report a false "not found". For those, reachability is established by the
        // enumeration below, whose Graph errors surface through the catch blocks instead.
        if (!CloudPath.IsSharePoint(path) && !Directory.Exists(path))
        {
            issues.Add($"[ERROR] {label} path not found or inaccessible: {path}");
            return (false, null, null);
        }

        try
        {
            long count = 0, bytes = 0;
            var excludes = new GlobMatcher(options.ExcludePatterns);
            var enumProgress = new Progress<EnumerationProgress>(p =>
                progress?.Report(new VerifyProgress
                {
                    Phase = phase,
                    Processed = p.FilesFound,
                    Message = p.CurrentDirectory is { } dir
                        ? $"Scanning {dir} ({p.FilesFound:N0} files)"
                        : $"Scanning {label}… ({p.FilesFound:N0} files)",
                }));

            await foreach (var record in _enumerator.EnumerateAsync(path, options, enumProgress, ct))
            {
                if (excludes.IsExcluded(record.RelativePath)) continue;

                // Assessed after exclusion: a file the run will never copy should not be reported
                // as a migration problem.
                if (migration is not null && migrationReport is not null && !record.IsDirectory)
                    migrationReport.Add(migration.Assess(record.RelativePath, record.SizeBytes), record.SizeBytes);
                count++;
                bytes += record.SizeBytes;
            }

            progress?.Report(new VerifyProgress { Phase = phase, Processed = count, Total = count, Message = $"{label}: {count:N0} files, {bytes:N0} bytes" });
            return (true, count, bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            issues.Add($"[ERROR] {label} access denied: {ex.Message}");
            return (false, null, null);
        }
        catch (IOException ex)
        {
            issues.Add($"[ERROR] {label} IO error: {ex.Message}");
            return (false, null, null);
        }
        catch (Exception ex) when (IsCloudFailure(ex))
        {
            // Sign-in, permission, and bad-path failures against Graph are the cloud equivalent of the
            // filesystem cases above: report them as a preflight issue rather than letting them abort
            // the whole check, since the point of preflight is to list what is wrong.
            issues.Add($"[ERROR] {label} not reachable: {ex.Message}");
            return (false, null, null);
        }
    }

    /// <summary>Destination limits used when assessing a cloud migration. Settable so a tenant with
    /// its own blocked-extension list or a tighter path budget can be modelled.</summary>
    public CloudLimits CloudLimits { get; set; } = new();

    /// <summary>Summarises migration findings as preflight issues.
    /// <para>Renames are a WARNING: they are expected, handled, and disclosed on the certificate.
    /// Blocked files and name collisions are ERRORS, because each means the destination would not
    /// receive a faithful copy of the source -- the thing the attestation is meant to establish.</para></summary>
    private static void AddMigrationIssues(List<string> issues, CloudMigrationReport report)
    {
        if (report.RenameCount > 0)
        {
            var sample = string.Join("; ", report.RenameSamples
                .Take(3).Select(r => $"\"{r.RelativePath}\" -> \"{r.SanitizedPath}\""));
            issues.Add(
                $"[WARN] {report.RenameCount:N0} file(s) will be renamed to satisfy destination naming " +
                $"rules (e.g. {sample}). The certificate records that these names were transformed.");
        }

        if (report.BlockedCount > 0)
        {
            var sample = string.Join("; ", report.BlockedSamples
                .Take(3).Select(b => $"\"{b.RelativePath}\" ({b.Blockers[0]})"));
            issues.Add(
                $"[ERROR] {report.BlockedCount:N0} file(s) cannot be migrated, totalling " +
                $"{report.BlockedBytes:N0} bytes (e.g. {sample}). These cannot be fixed by renaming.");
        }

        if (report.Collisions.Count > 0)
        {
            var sample = string.Join("; ", report.Collisions
                .Take(3).Select(c => $"\"{c.First}\" and \"{c.Second}\" both become \"{c.Sanitized}\""));
            issues.Add(
                $"[ERROR] {report.Collisions.Count:N0} name collision(s) after sanitizing: {sample}. " +
                "One file would overwrite the other; rename one at the source first.");
        }
    }

    /// <summary>Attempts a real write to the destination and removes it again.
    /// <para>A tiny uniquely-named file, so a concurrent run cannot collide and a failed cleanup is
    /// identifiable by name. Target-agnostic: this answers "can this account write here" for an SMB
    /// share just as usefully as for a SharePoint library.</para></summary>
    private static async Task<WriteProbeResult> ProbeWriteAsync(
        IFileTarget target, List<string> issues, CancellationToken cancellationToken)
    {
        // Named so that if cleanup fails, whoever finds it knows what it is and that it is disposable.
        var probePath = $"filedrift-write-probe-{Guid.NewGuid():N}.tmp";
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "FileDrift write-access probe. Safe to delete.");
        var now = DateTime.UtcNow;

        try
        {
            var written = await target.WriteFileAsync(new FileWriteRequest(
                probePath,
                _ => Task.FromResult<Stream>(new MemoryStream(payload)),
                payload.Length,
                now,
                now), cancellationToken).ConfigureAwait(false);

            // Clean up even if the byte count looks wrong -- something was created either way.
            var cleaned = await target.TryDeletePartialAsync(
                written.FinalRelativePath, CancellationToken.None).ConfigureAwait(false);

            if (!cleaned)
                issues.Add(
                    $"[WARN] The write probe succeeded but its temporary file could not be removed. " +
                    $"Delete \"{written.FinalRelativePath}\" from the destination by hand.");

            return new WriteProbeResult
            {
                Attempted = true,
                Succeeded = true,
                CleanedUp = cleaned,
                ProbePath = written.FinalRelativePath,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An ERROR, not a warning: a destination that cannot be written to will fail a reconcile,
            // and finding that out now is the entire purpose of a preflight.
            issues.Add($"[ERROR] Cannot write to the destination: {ex.Message}");
            return new WriteProbeResult
            {
                Attempted = true,
                Succeeded = false,
                ProbePath = probePath,
                Error = ex.Message,
            };
        }
    }

    private static bool IsCloudFailure(Exception ex) =>
        ex is Cloud.GraphAuthException or Cloud.GraphApiException;
}
