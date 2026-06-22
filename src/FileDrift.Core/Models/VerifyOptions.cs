namespace FileDrift.Core.Models;

public sealed class VerifyOptions
{
    public VerifyDepth Depth { get; init; } = VerifyDepth.Standard;
    public FileDriftHashAlgorithm HashAlgorithm { get; init; } = FileDriftHashAlgorithm.SHA256;
    public bool IncludeAcl { get; init; }

    /// <summary>Default parallelism: 50% of logical processors (at least 1).</summary>
    public int Threads { get; init; } = DefaultThreads;

    /// <summary>50% of logical processors, floored at 1.</summary>
    public static int DefaultThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Glob patterns for paths to exclude, relative to the root being scanned.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
}
