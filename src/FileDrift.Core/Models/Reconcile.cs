namespace FileDrift.Core.Models;

/// <summary>What a reconcile step does to the destination file.</summary>
public enum ReconcileActionKind
{
    /// <summary>Destination file does not exist — copy it from source.</summary>
    Copy,
    /// <summary>Destination file exists but differs — overwrite it with source.</summary>
    Overwrite,
}

/// <summary>A single planned source→destination copy.</summary>
public sealed class ReconcileAction
{
    public required string RelativePath { get; init; }
    public required string SourceFullPath { get; init; }
    public required string DestFullPath { get; init; }
    public required ReconcileActionKind Kind { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>True when this overwrite replaces a destination file whose last-write time is
    /// newer than the source — i.e. it clobbers content that changed after the source copy.</summary>
    public bool ClobbersNewer { get; init; }

    /// <summary>Whether the file's bytes are (re)written. False for an ACL-only fix.</summary>
    public bool CopyContent { get; init; } = true;

    /// <summary>When non-null, the source SDDL to apply to the destination after copying
    /// (so permissions match). Null when ACL reconciliation is off or not needed.</summary>
    public string? ApplyAclSddl { get; init; }
}

/// <summary>The full set of planned reconcile actions, derived from a comparison result set.
/// Building a plan performs no I/O; it is safe to compute for preview.</summary>
public sealed class ReconcilePlan
{
    public required IReadOnlyList<ReconcileAction> Actions { get; init; }

    public int CopyCount { get; init; }
    public int OverwriteCount { get; init; }
    /// <summary>How many of the overwrites replace a newer destination file.</summary>
    public int ClobberCount { get; init; }
    /// <summary>How many actions also (or only) apply the source's permissions to the destination.</summary>
    public int AclCount { get; init; }
    public long TotalBytes { get; init; }

    public int TotalActions => Actions.Count;
}

public sealed class ReconcileProgress
{
    public required int Processed { get; init; }
    public required int Total { get; init; }
    public string? Message { get; init; }
}

/// <summary>A file that could not be copied during reconcile.</summary>
public sealed class ReconcileFailure
{
    public required string RelativePath { get; init; }
    public required string Error { get; init; }
}

public sealed class ReconcileResult
{
    public required int Copied { get; init; }
    public required int Overwritten { get; init; }
    public required long BytesCopied { get; init; }
    public required IReadOnlyList<ReconcileFailure> Failures { get; init; }

    /// <summary>How many destination files had their permissions (ACL) set from the source.</summary>
    public int AclsApplied { get; init; }

    public int FailureCount => Failures.Count;
}
