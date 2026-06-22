namespace FileDrift.Core.Models;

public sealed class RunQueryOptions
{
    public string? SourcePath { get; init; }
    public string? DestPath { get; init; }
    public RunStatus? Status { get; init; }
    public DateTime? After { get; init; }
    public DateTime? Before { get; init; }

    /// <summary>Maximum number of records to return; null returns all matching records.</summary>
    public int? Limit { get; init; }
}
