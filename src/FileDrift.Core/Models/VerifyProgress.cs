namespace FileDrift.Core.Models;

public enum VerifyPhase
{
    EnumeratingSource,
    EnumeratingDestination,
    Enriching,   // hashing and/or ACL reads on matched pairs
    Comparing,
    Persisting,
    Done,
}

public sealed class VerifyProgress
{
    public required VerifyPhase Phase { get; init; }
    public long Processed { get; init; }

    /// <summary>Total expected for this phase, or 0 when not yet known (e.g. mid-enumeration).</summary>
    public long Total { get; init; }

    public string? Message { get; init; }
}
