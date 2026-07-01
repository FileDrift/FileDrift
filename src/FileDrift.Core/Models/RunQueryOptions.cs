// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

public sealed class RunQueryOptions
{
    public string? SourcePath { get; init; }
    public string? DestPath { get; init; }
    public RunStatus? Status { get; init; }
    public DateTime? After { get; init; }
    public DateTime? Before { get; init; }

    /// <summary>Filter by sign-off state: true = signed-off runs only, false = unsigned runs only,
    /// null (default) = both.</summary>
    public bool? SignedOff { get; init; }

    /// <summary>Maximum number of records to return; null returns all matching records.</summary>
    public int? Limit { get; init; }
}
