namespace FileDrift.Core.Models;

/// <summary>The outcome of a verify run: the persisted summary plus the full per-file comparison set.
/// The <see cref="RunRecord"/> summary is stored in history; <see cref="Comparisons"/> is returned
/// for immediate display/export but is not persisted in v1.</summary>
public sealed class VerifyResult
{
    public required RunRecord Run { get; init; }
    public required IReadOnlyList<ComparisonResult> Comparisons { get; init; }
}
