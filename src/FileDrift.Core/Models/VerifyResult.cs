// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

/// <summary>The outcome of a verify run: the persisted summary plus the full per-file comparison set.
/// The <see cref="RunRecord"/> summary is stored in history; <see cref="Comparisons"/> is returned
/// for immediate display/export but is not persisted in v1.</summary>
public sealed class VerifyResult
{
    public required RunRecord Run { get; init; }
    public required IReadOnlyList<ComparisonResult> Comparisons { get; init; }

    /// <summary>Number of destination-only files excluded by the End cutoff (newer than End with no
    /// source counterpart). Zero when no cutoff is set.</summary>
    public long ExcludedNewerCount { get; init; }

    /// <summary>Source/destination paths that could not be read (access denied or I/O error) and were
    /// skipped during enumeration. Non-empty means the comparison is incomplete — a "zero drift"
    /// sign-off must account for these. Surfaced in the summary and written to the run log.</summary>
    public IReadOnlyList<string> InaccessiblePaths { get; init; } = [];
}
