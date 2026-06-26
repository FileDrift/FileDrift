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

    public long TotalDifferences => DifferentCount + MissingAtDestCount + ExtraAtDestCount;
}
