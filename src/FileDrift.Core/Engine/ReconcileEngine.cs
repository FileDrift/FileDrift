using System.Net;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Copies files source→destination to make the destination match the source for the
/// differences found by a verify run. Strictly one-directional — the source is the source of truth;
/// nothing is ever written back to the source, and Extra-at-dest files are never deleted.</summary>
public sealed class ReconcileEngine
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>Builds the copy plan from comparison results. Pure (no I/O) — safe for preview.
    /// Acts only on MissingAtDest (copy) and Different (overwrite); directories and Extra/Matched
    /// entries are ignored.</summary>
    public static ReconcilePlan BuildPlan(IEnumerable<ComparisonResult> comparisons, string destRoot)
    {
        var actions = new List<ReconcileAction>();

        foreach (var c in comparisons)
        {
            switch (c.Status)
            {
                case ComparisonStatus.MissingAtDest when c.Source is { IsDirectory: false } s:
                    actions.Add(new ReconcileAction
                    {
                        RelativePath = c.RelativePath,
                        SourceFullPath = s.FullPath,
                        DestFullPath = Path.Combine(destRoot, c.RelativePath),
                        Kind = ReconcileActionKind.Copy,
                        SizeBytes = s.SizeBytes,
                        ClobbersNewer = false,
                    });
                    break;

                case ComparisonStatus.Different when c.Source is { IsDirectory: false } s && c.Dest is { } d:
                    actions.Add(new ReconcileAction
                    {
                        RelativePath = c.RelativePath,
                        SourceFullPath = s.FullPath,
                        DestFullPath = d.FullPath,
                        Kind = ReconcileActionKind.Overwrite,
                        SizeBytes = s.SizeBytes,
                        ClobbersNewer = d.LastWriteTimeUtc > s.LastWriteTimeUtc,
                    });
                    break;
            }
        }

        return new ReconcilePlan
        {
            Actions = actions,
            CopyCount = actions.Count(a => a.Kind == ReconcileActionKind.Copy),
            OverwriteCount = actions.Count(a => a.Kind == ReconcileActionKind.Overwrite),
            ClobberCount = actions.Count(a => a.ClobbersNewer),
            TotalBytes = actions.Sum(a => a.SizeBytes),
        };
    }

    /// <summary>Executes the plan, copying each file source→destination. Authenticates UNC shares
    /// with the supplied credentials (the destination credential needs write access). Per-file
    /// errors are collected rather than aborting the whole run.</summary>
    public async Task<ReconcileResult> ExecuteAsync(
        ReconcilePlan plan,
        string sourcePath,
        string destPath,
        NetworkCredential? sourceCredential = null,
        NetworkCredential? destCredential = null,
        IProgress<ReconcileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var connections = new List<IDisposable>();
        int copied = 0, overwritten = 0;
        long bytes = 0;
        var failures = new List<ReconcileFailure>();

        try
        {
            if (sourceCredential is not null && NetworkPath.IsUnc(sourcePath))
                connections.Add(new NetworkConnection(NetworkPath.GetShareRoot(sourcePath), sourceCredential));
            if (destCredential is not null && NetworkPath.IsUnc(destPath))
                connections.Add(new NetworkConnection(NetworkPath.GetShareRoot(destPath), destCredential));

            int total = plan.Actions.Count, n = 0;
            foreach (var a in plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                n++;
                try
                {
                    await CopyFileAsync(a.SourceFullPath, a.DestFullPath, cancellationToken);
                    if (a.Kind == ReconcileActionKind.Copy) copied++; else overwritten++;
                    bytes += a.SizeBytes;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add(new ReconcileFailure
                    {
                        RelativePath = a.RelativePath,
                        Error = $"{ex.GetType().Name}: {ex.Message}",
                    });
                }

                progress?.Report(new ReconcileProgress
                {
                    Processed = n,
                    Total = total,
                    Message = $"{(a.Kind == ReconcileActionKind.Copy ? "Copy" : "Overwrite")} {a.RelativePath}",
                });
            }
        }
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }

        return new ReconcileResult
        {
            Copied = copied,
            Overwritten = overwritten,
            BytesCopied = bytes,
            Failures = failures,
        };
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                         BufferSize, FileOptions.Asynchronous))
        {
            await src.CopyToAsync(dst, BufferSize, ct);
        }

        // Preserve source timestamps so a follow-up verify reports the pair as matched.
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
    }
}
