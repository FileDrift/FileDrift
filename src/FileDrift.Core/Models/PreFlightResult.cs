// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

public sealed class PreFlightResult
{
    public required Guid RunId { get; init; }
    public required DateTime CheckedAtUtc { get; init; }
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }

    public bool SourceAccessible { get; init; }
    public bool DestAccessible { get; init; }

    public long? SourceFileCount { get; init; }
    public long? DestFileCount { get; init; }
    public long? SourceTotalBytes { get; init; }
    public long? DestTotalBytes { get; init; }

    /// <summary>Human-readable issues that block or warn about the run.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>True when both paths are accessible and no blocking issues exist.</summary>
    public bool IsReady => SourceAccessible && DestAccessible && !Issues.Any(i => i.StartsWith("[ERROR]"));
}
