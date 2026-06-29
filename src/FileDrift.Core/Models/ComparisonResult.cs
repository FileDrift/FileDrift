// SPDX-License-Identifier: GPL-3.0-or-later
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

    /// <summary>Human-readable summary of an explicit-ACL difference (e.g. "2 missing on dest, 1 extra on dest,
    /// owner differs"), set only when <see cref="Differences"/> includes <see cref="FileDifference.Acl"/>.</summary>
    public string? AclDetail { get; init; }
}
