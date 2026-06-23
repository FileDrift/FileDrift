namespace FileDrift.Core.Models;

public sealed record VerifyOptions
{
    public VerifyDepth Depth { get; init; } = VerifyDepth.Standard;

    /// <summary>Default MD5 — fast and fine for drift detection. Strict mode forces SHA-256.</summary>
    public FileDriftHashAlgorithm HashAlgorithm { get; init; } = FileDriftHashAlgorithm.MD5;

    /// <summary>Compare explicit (non-inherited) DACL permissions between source and destination.
    /// Enumerates directories too (folders are where explicit permissions usually live).</summary>
    public bool IncludeAcl { get; init; }

    /// <summary>When comparing ACLs, also require the owner to match. Optional; off by default
    /// (owners often differ across servers without being meaningful drift). Strict forces it on.</summary>
    public bool EnforceOwnership { get; init; }

    /// <summary>Default parallelism: 50% of logical processors (at least 1).</summary>
    public int Threads { get; init; } = DefaultThreads;

    /// <summary>50% of logical processors, floored at 1.</summary>
    public static int DefaultThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Glob patterns for paths to exclude, relative to the root being scanned.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>Exact-match mode: forces Full depth, SHA-256, ACL comparison, and zero timestamp
    /// tolerance. Any byte, permission, or timestamp difference counts as a difference.</summary>
    public bool Strict { get; init; }

    /// <summary>Optional lower bound on Last-Modified time (UTC, inclusive). SYMMETRIC: files modified
    /// before this on <em>either</em> side are ignored entirely — e.g. when consolidating into a
    /// destination that already holds older content. Null = no lower bound.</summary>
    public DateTime? StartUtc { get; init; }

    /// <summary>Optional upper bound on Last-Modified time (UTC, inclusive). ASYMMETRIC: a destination
    /// file modified after this is excluded only if it has <em>no</em> source counterpart (post-migration
    /// noise on a live destination). Files present on both sides are always compared regardless of age,
    /// and the source side is never filtered. Null = no upper bound.</summary>
    public DateTime? EndUtc { get; init; }

    /// <summary>Inclusive end-of-day UTC from a local calendar date (e.g. "2026-02-06" → its last tick).</summary>
    public static DateTime EndOfLocalDayUtc(DateTime localDate) =>
        DateTime.SpecifyKind(localDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

    /// <summary>Inclusive start-of-day UTC from a local calendar date (e.g. "2026-02-06" → 00:00 local).</summary>
    public static DateTime StartOfLocalDayUtc(DateTime localDate) =>
        DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local).ToUniversalTime();

    /// <summary>Applies Strict overrides, returning the options the engine should actually run with.
    /// Non-strict options are returned unchanged.</summary>
    public VerifyOptions AsEffective() => Strict
        ? this with
        {
            Depth = VerifyDepth.Full,
            HashAlgorithm = FileDriftHashAlgorithm.SHA256,
            IncludeAcl = true,
            EnforceOwnership = true,
        }
        : this;
}
