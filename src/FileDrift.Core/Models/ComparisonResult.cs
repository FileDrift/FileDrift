namespace FileDrift.Core.Models;

public sealed class ComparisonResult
{
    public required string RelativePath { get; init; }
    public required ComparisonStatus Status { get; init; }

    /// <summary>Meaningful only when Status == Different.</summary>
    public FileDifference Differences { get; init; }

    /// <summary>Null when Status == ExtraAtDest.</summary>
    public FileRecord? Source { get; init; }

    /// <summary>Null when Status == MissingAtDest.</summary>
    public FileRecord? Dest { get; init; }
}
