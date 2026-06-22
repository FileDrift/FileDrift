namespace FileDrift.Core.Models;

public sealed class VerifyOptions
{
    public VerifyDepth Depth { get; init; } = VerifyDepth.Standard;
    public FileDriftHashAlgorithm HashAlgorithm { get; init; } = FileDriftHashAlgorithm.SHA256;
    public bool IncludeAcl { get; init; }
    public int Threads { get; init; } = 8;

    /// <summary>Glob patterns for paths to exclude, relative to the root being scanned.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
}
