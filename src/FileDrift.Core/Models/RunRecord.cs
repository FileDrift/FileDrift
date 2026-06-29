// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

public sealed class RunRecord
{
    public required Guid Id { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }
    public required VerifyOptions Options { get; init; }

    public DateTime? CompletedAtUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.InProgress;

    public long TotalSourceFiles { get; set; }
    public long TotalDestFiles { get; set; }
    public long MatchedCount { get; set; }
    public long DifferentCount { get; set; }
    public long MissingAtDestCount { get; set; }
    public long ExtraAtDestCount { get; set; }

    /// <summary>Paths that could not be read (access denied / I/O error) and were skipped during
    /// enumeration. Non-zero means the comparison was incomplete.</summary>
    public long InaccessibleCount { get; set; }

    /// <summary>Optional free-text sign-off note written after reviewing results.</summary>
    public string? SignOffNote { get; set; }

    /// <summary>UTC timestamp of sign-off; null until the run is signed off.</summary>
    public DateTime? SignedOffAtUtc { get; set; }

    /// <summary>The accountable party recorded at sign-off. Defaults to the Windows account that performed
    /// the sign-off (<see cref="SignedOffByAccount"/>) but may be overridden by the operator – e.g. signing
    /// on behalf of a named approver. Null until the run is signed off.</summary>
    public string? SignedOffBy { get; set; }

    /// <summary>The Windows account that actually performed the sign-off (DOMAIN\user), captured
    /// automatically and never editable. Kept alongside <see cref="SignedOffBy"/> so an overridden
    /// approver name never erases who operated the tool. Null until the run is signed off.</summary>
    public string? SignedOffByAccount { get; set; }

    /// <summary>True when the recorded approver differs from the operating account – worth surfacing in a
    /// report so a reviewer sees the sign-off was entered on someone else's behalf.</summary>
    public bool SignOffWasDelegated =>
        SignedOffAtUtc is not null &&
        !string.Equals(SignedOffBy, SignedOffByAccount, StringComparison.OrdinalIgnoreCase);

    public long TotalDifferences => DifferentCount + MissingAtDestCount + ExtraAtDestCount;
}
