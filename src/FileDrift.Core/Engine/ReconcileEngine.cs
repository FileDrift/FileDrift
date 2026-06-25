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

    /// <summary>Builds the reconcile plan from comparison results. Pure (no I/O) — safe for preview.
    /// Copies missing files, overwrites content-different files, and creates missing folders. When
    /// <paramref name="includeAcls"/> is set, additively applies the source's explicit (non-inherited)
    /// ACEs that the destination lacks — never removing the destination's own permissions. When
    /// <paramref name="enforceOwnership"/> is set, also sets the source owner where it differs.</summary>
    public static ReconcilePlan BuildPlan(
        IEnumerable<ComparisonResult> comparisons, string destRoot,
        bool includeAcls = false, bool enforceOwnership = false)
    {
        var actions = new List<ReconcileAction>();

        foreach (var c in comparisons)
        {
            switch (c.Status)
            {
                case ComparisonStatus.MissingAtDest when c.Source is { } s:
                {
                    var aces = includeAcls ? AclModel.ExplicitAces(s.SecurityDescriptor).ToArray() : [];
                    actions.Add(new ReconcileAction
                    {
                        RelativePath = c.RelativePath,
                        SourceFullPath = s.FullPath,
                        DestFullPath = Path.Combine(destRoot, c.RelativePath),
                        Kind = ReconcileActionKind.Copy,
                        SizeBytes = s.IsDirectory ? 0 : s.SizeBytes,
                        ClobbersNewer = false,
                        CopyContent = !s.IsDirectory,
                        CreateDirectory = s.IsDirectory,
                        AddExplicitAces = aces.Length > 0 ? aces : null,
                        SetOwnerSid = enforceOwnership ? AclModel.Owner(s.SecurityDescriptor) : null,
                    });
                    break;
                }

                case ComparisonStatus.Different when c.Source is { } s && c.Dest is { } d:
                {
                    bool contentDiffers = !s.IsDirectory && (c.Differences & ContentDifferences) != 0;

                    IReadOnlyList<string>? addAces = null;
                    string? setOwner = null;
                    if (includeAcls && (c.Differences & FileDifference.Acl) != 0)
                    {
                        var delta = AclModel.CompareExplicit(s.SecurityDescriptor, d.SecurityDescriptor);
                        if (delta.DestMissing.Count > 0) addAces = delta.DestMissing; // additive: only what dest lacks
                        if (enforceOwnership &&
                            !string.Equals(AclModel.Owner(s.SecurityDescriptor), AclModel.Owner(d.SecurityDescriptor), StringComparison.OrdinalIgnoreCase))
                            setOwner = AclModel.Owner(s.SecurityDescriptor);
                    }

                    if (!contentDiffers && addAces is null && setOwner is null)
                        break; // e.g. only dest-EXTRA explicit ACEs — reported, never removed

                    actions.Add(new ReconcileAction
                    {
                        RelativePath = c.RelativePath,
                        SourceFullPath = s.FullPath,
                        DestFullPath = d.FullPath,
                        Kind = ReconcileActionKind.Overwrite,
                        SizeBytes = contentDiffers ? s.SizeBytes : 0,
                        ClobbersNewer = contentDiffers && d.LastWriteTimeUtc > s.LastWriteTimeUtc,
                        CopyContent = contentDiffers,
                        AddExplicitAces = addAces,
                        SetOwnerSid = setOwner,
                    });
                    break;
                }
            }
        }

        return new ReconcilePlan
        {
            Actions = actions,
            CopyCount = actions.Count(a => a.CopyContent && a.Kind == ReconcileActionKind.Copy),
            OverwriteCount = actions.Count(a => a.CopyContent && a.Kind == ReconcileActionKind.Overwrite),
            DirCreateCount = actions.Count(a => a.CreateDirectory),
            ClobberCount = actions.Count(a => a.ClobbersNewer),
            AclCount = actions.Count(a => a.TouchesAcl),
            TotalBytes = actions.Where(a => a.CopyContent).Sum(a => a.SizeBytes),
        };
    }

    /// <summary>Executes the plan, copying each file source→destination. Authenticates UNC shares
    /// with the supplied credentials (the destination credential needs write access). Per-file
    /// errors are collected rather than aborting the whole run. Two stop signals:
    /// <paramref name="hardCancel"/> aborts the in-flight file mid-copy and deletes the partial, then
    /// stops; <paramref name="softStop"/> lets the current file finish, then stops before the next.</summary>
    public async Task<ReconcileResult> ExecuteAsync(
        ReconcilePlan plan,
        string sourcePath,
        string destPath,
        NetworkCredential? sourceCredential = null,
        NetworkCredential? destCredential = null,
        IProgress<ReconcileProgress>? progress = null,
        CancellationToken hardCancel = default,
        CancellationToken softStop = default)
    {
        const long ReportEvery = 32L * 1024 * 1024; // throttle byte progress so huge files don't flood
        var connections = new List<IDisposable>();
        int copied = 0, overwritten = 0, aclsApplied = 0, dirsCreated = 0, partialsRemoved = 0;
        long bytes = 0, lastReported = 0;
        long totalBytes = plan.TotalBytes;
        bool stopped = false;
        string? lastCompleted = null; // last action that fully succeeded, for an accurate stop message
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
                // Stop before starting the next file. A soft stop reaches here only after the current
                // file finished; a hard cancel between files also stops here.
                if (hardCancel.IsCancellationRequested || softStop.IsCancellationRequested)
                {
                    stopped = true;
                    // Name the real last file copied (the throttled screen log may have sampled past it).
                    if (lastCompleted is not null)
                        progress?.Report(new ReconcileProgress
                        {
                            Processed = n, Total = total, BytesCopied = bytes, TotalBytes = totalBytes,
                            Message = $"Stopped – last file copied: {lastCompleted}", Important = true,
                        });
                    break;
                }
                n++;
                try
                {
                    if (a.CreateDirectory)
                    {
                        Directory.CreateDirectory(a.DestFullPath);
                        dirsCreated++;
                    }

                    if (a.CopyContent)
                    {
                        await CopyFileAsync(a.SourceFullPath, a.DestFullPath, fileBytes =>
                        {
                            long now = bytes + fileBytes;
                            if (now - lastReported >= ReportEvery)
                            {
                                lastReported = now;
                                progress?.Report(new ReconcileProgress
                                {
                                    Processed = n, Total = total, BytesCopied = now, TotalBytes = totalBytes,
                                    Message = $"Copying {a.RelativePath}",
                                });
                            }
                        }, hardCancel);

                        bytes += a.SizeBytes;
                        lastReported = bytes;
                        if (a.Kind == ReconcileActionKind.Copy) copied++; else overwritten++;
                    }

                    bool aclTouched = false;
                    if (a.AddExplicitAces is { Count: > 0 } aces)
                    {
                        var err = _aclWriter.TryApplyExplicitAces(a.DestFullPath, aces);
                        if (err is null) aclTouched = true;
                        else failures.Add(new ReconcileFailure { RelativePath = a.RelativePath, Error = $"ACL: {err}" });
                    }
                    if (a.SetOwnerSid is { } owner)
                    {
                        var err = _aclWriter.TrySetOwner(a.DestFullPath, owner);
                        if (err is null) aclTouched = true;
                        else failures.Add(new ReconcileFailure { RelativePath = a.RelativePath, Error = $"owner: {err}" });
                    }
                    if (aclTouched) aclsApplied++;
                    lastCompleted = a.RelativePath; // only on full success
                }
                catch (OperationCanceledException)
                {
                    // Hard cancel mid-file: remove the partially-written destination file, then stop.
                    if (a.CopyContent && TryDeletePartial(a.DestFullPath))
                    {
                        partialsRemoved++;
                        progress?.Report(new ReconcileProgress
                        {
                            Processed = n, Total = total, BytesCopied = bytes, TotalBytes = totalBytes,
                            Message = $"Cleanup – deleted partial copy: {a.RelativePath}", Important = true,
                        });
                    }
                    stopped = true;
                    break;
                }
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
                    Processed = n, Total = total, BytesCopied = bytes, TotalBytes = totalBytes,
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
            DirectoriesCreated = dirsCreated,
            Stopped = stopped,
            PartialsRemoved = partialsRemoved,
            Failures = failures,
        };
    }

    private static bool TryDeletePartial(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); return true; } }
        catch { /* best-effort cleanup */ }
        return false;
    }

    /// <summary>Describes what an action does, for progress/log display.</summary>
    public static string ActionVerb(ReconcileAction a)
    {
        var verb = a switch
        {
            { CreateDirectory: true } => "Create dir",
            { CopyContent: true, Kind: ReconcileActionKind.Copy } => "Copy",
            { CopyContent: true } => "Overwrite",
            _ => null,
        };
        return (verb, a.TouchesAcl) switch
        {
            (null, true) => "Set ACL",
            (not null, true) => verb + "+ACL",
            (not null, false) => verb!,
            _ => "Skip",
        };
    }

    /// <summary>Copies a file in buffer-sized chunks, invoking <paramref name="onFileBytes"/> with the
    /// running per-file byte count after each chunk (for live progress). Honors cancellation between
    /// chunks (within a buffer), so a hard cancel stops promptly.</summary>
    private static async Task CopyFileAsync(string source, string dest, Action<long> onFileBytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                         BufferSize, FileOptions.Asynchronous))
        {
            var buffer = new byte[BufferSize];
            long fileBytes = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                fileBytes += read;
                onFileBytes(fileBytes);
            }
        }

        // Preserve source timestamps so a follow-up verify reports the pair as matched.
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
    }
}
