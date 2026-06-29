// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
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
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var connections = new List<IDisposable>();

        bool sourceOk = false, destOk = false;
        long? sourceCount = null, sourceBytes = null, destCount = null, destBytes = null;

        try
        {
            TryConnect(connections, sourcePath, sourceCredential, "source", issues);
            TryConnect(connections, destPath, destCredential, "destination", issues);

            (sourceOk, sourceCount, sourceBytes) =
                await ProbeAsync(sourcePath, options, VerifyPhase.EnumeratingSource, "source", issues, progress, cancellationToken);
            (destOk, destCount, destBytes) =
                await ProbeAsync(destPath, options, VerifyPhase.EnumeratingDestination, "destination", issues, progress, cancellationToken);
        }
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }

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
        CancellationToken ct)
    {
        if (!Directory.Exists(path))
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
    }
}
