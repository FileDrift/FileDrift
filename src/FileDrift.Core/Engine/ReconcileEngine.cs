using System.Net;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Copies files source→destination to make the destination match the source for the
/// differences found by a verify run. Strictly one-directional — the source is the source of truth;
/// nothing is ever written back to the source, and Extra-at-dest files are never deleted.</summary>
public sealed class ReconcileEngine
{
    private const int BufferSize = 1 << 20; // 1 MiB
    private const FileDifference ContentDifferences = FileDifference.Size | FileDifference.Timestamp | FileDifference.Hash;

    private readonly AclWriter _aclWriter = new();

    /// <summary>Builds the copy plan from comparison results. Pure (no I/O) — safe for preview.
    /// Acts only on MissingAtDest (copy) and Different (overwrite); directories and Extra/Matched
    /// entries are ignored. When <paramref name="includeAcls"/> is set, also copies the source's
    /// permissions (SDDL) so destination ACLs match — and an ACL-only difference is fixed without
    /// rewriting the file's bytes.</summary>
    public static ReconcilePlan BuildPlan(IEnumerable<ComparisonResult> comparisons, string destRoot, bool includeAcls = false)
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
                        CopyContent = true,
                        ApplyAclSddl = includeAcls ? s.SecurityDescriptor : null, // new file → match source ACL
                    });
                    break;

                case ComparisonStatus.Different when c.Source is { IsDirectory: false } s && c.Dest is { } d:
                    bool contentDiffers = (c.Differences & ContentDifferences) != 0;
                    bool aclDiffers = includeAcls && (c.Differences & FileDifference.Acl) != 0 && s.SecurityDescriptor is not null;
                    if (!contentDiffers && !aclDiffers)
                        break; // nothing actionable (e.g. ACL-only diff but ACL reconciliation off)

                    actions.Add(new ReconcileAction
                    {
                        RelativePath = c.RelativePath,
                        SourceFullPath = s.FullPath,
                        DestFullPath = d.FullPath,
                        Kind = ReconcileActionKind.Overwrite,
                        SizeBytes = s.SizeBytes,
                        ClobbersNewer = d.LastWriteTimeUtc > s.LastWriteTimeUtc,
                        CopyContent = contentDiffers,
                        ApplyAclSddl = aclDiffers ? s.SecurityDescriptor : null,
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
            AclCount = actions.Count(a => a.ApplyAclSddl is not null),
            TotalBytes = actions.Where(a => a.CopyContent).Sum(a => a.SizeBytes),
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
        int copied = 0, overwritten = 0, aclsApplied = 0;
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
                    if (a.CopyContent)
                    {
                        await CopyFileAsync(a.SourceFullPath, a.DestFullPath, cancellationToken);
                        if (a.Kind == ReconcileActionKind.Copy) copied++; else overwritten++;
                        bytes += a.SizeBytes;
                    }

                    if (a.ApplyAclSddl is { } sddl)
                    {
                        var aclError = _aclWriter.TryApplySddl(a.DestFullPath, sddl);
                        if (aclError is null) aclsApplied++;
                        else failures.Add(new ReconcileFailure { RelativePath = a.RelativePath, Error = $"ACL: {aclError}" });
                    }
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
                    Message = $"{ActionVerb(a)} {a.RelativePath}",
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
            AclsApplied = aclsApplied,
            Failures = failures,
        };
    }

    /// <summary>Describes what an action does, for progress/log display.</summary>
    public static string ActionVerb(ReconcileAction a) => (a.CopyContent, a.ApplyAclSddl is not null) switch
    {
        (true, true) => a.Kind == ReconcileActionKind.Copy ? "Copy+ACL" : "Overwrite+ACL",
        (true, false) => a.Kind == ReconcileActionKind.Copy ? "Copy" : "Overwrite",
        (false, true) => "Set ACL",
        _ => "Skip",
    };

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
