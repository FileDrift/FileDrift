// SPDX-License-Identifier: GPL-3.0-or-later
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

    /// <summary>Create the destination directory first (for a missing source folder).</summary>
    public bool CreateDirectory { get; init; }

    /// <summary>Explicit (non-inherited) source ACE bodies to ensure on the destination, merged with
    /// the destination's existing explicit ACEs (additive). Null/empty when no ACL change is needed.</summary>
    public IReadOnlyList<string>? AddExplicitAces { get; init; }

    /// <summary>Source owner SID to apply to the destination (only when owner enforcement is on
    /// and the owner differs). Null otherwise.</summary>
    public string? SetOwnerSid { get; init; }

    public bool TouchesAcl => (AddExplicitAces is { Count: > 0 }) || SetOwnerSid is not null;
}

/// <summary>The full set of planned reconcile actions, derived from a comparison result set.
/// Building a plan performs no I/O; it is safe to compute for preview.</summary>
public sealed class ReconcilePlan
{
    public required IReadOnlyList<ReconcileAction> Actions { get; init; }

    public int CopyCount { get; init; }
    public int OverwriteCount { get; init; }
    /// <summary>How many missing source folders will be created on the destination.</summary>
    public int DirCreateCount { get; init; }
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

    /// <summary>Total bytes copied so far across all files (for a byte-accurate progress bar).</summary>
    public long BytesCopied { get; init; }
    /// <summary>Total bytes the plan will write (plan.TotalBytes).</summary>
    public long TotalBytes { get; init; }

    /// <summary>When set, the on-screen log should show <see cref="Message"/> verbatim regardless of
    /// throttling (e.g. a cleanup notice). Routine per-file progress leaves this false.</summary>
    public bool Important { get; init; }
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

    /// <summary>How many destination entries had permissions (ACL/owner) applied from the source.</summary>
    public int AclsApplied { get; init; }

    /// <summary>How many missing destination folders were created.</summary>
    public int DirectoriesCreated { get; init; }

    /// <summary>True if the run stopped early (cancelled) before processing every action.</summary>
    public bool Stopped { get; init; }

    /// <summary>How many partially-written destination files were deleted on a hard cancel.</summary>
    public int PartialsRemoved { get; init; }

    public int FailureCount => Failures.Count;
}
