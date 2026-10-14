// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

public sealed class ComparisonResult
{
    public required string RelativePath { get; init; }
    public required ComparisonStatus Status { get; init; }

    /// <summary>The name this entry has at the destination, when it differs from
    /// <see cref="RelativePath"/> — set only where the destination could not accept the source
    /// name and it had to be transformed. Null in the ordinary case, so a rename is something a
    /// report states explicitly rather than a difference a reader has to infer.</summary>
    public string? DestinationRelativePath { get; init; }

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
