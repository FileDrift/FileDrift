namespace FileDrift.Core.Models;

public sealed record VerifyOptions
{
    public VerifyDepth Depth { get; init; } = VerifyDepth.Standard;

    /// <summary>Default MD5 — fast and fine for drift detection. Strict mode forces SHA-256.</summary>
    public FileDriftHashAlgorithm HashAlgorithm { get; init; } = FileDriftHashAlgorithm.MD5;

    public bool IncludeAcl { get; init; }

    /// <summary>Default parallelism: 50% of logical processors (at least 1).</summary>
    public int Threads { get; init; } = DefaultThreads;

    /// <summary>50% of logical processors, floored at 1.</summary>
    public static int DefaultThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Glob patterns for paths to exclude, relative to the root being scanned.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>Exact-match mode: forces Full depth, SHA-256, ACL comparison, and zero timestamp
    /// tolerance. Any byte, permission, or timestamp difference counts as a difference.</summary>
    public bool Strict { get; init; }

    /// <summary>Optional "as-of" cutoff (inclusive upper bound, UTC). When set, destination files
    /// modified after this instant that have <em>no</em> source counterpart (i.e. would be reported
    /// as Extra-at-dest) are treated as out of scope and excluded from the report — the post-migration
    /// noise on a still-live destination. Files present on both sides are always compared regardless
    /// of age, and the source side is never filtered (it is the source of truth).</summary>
    public DateTime? AsOfUtc { get; init; }

    /// <summary>Builds an inclusive end-of-day UTC cutoff from a local calendar date
    /// (e.g. "2026-02-06" → last tick of that day, local, expressed in UTC).</summary>
    public static DateTime EndOfLocalDayUtc(DateTime localDate) =>
        DateTime.SpecifyKind(localDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

    /// <summary>Applies Strict overrides, returning the options the engine should actually run with.
    /// Non-strict options are returned unchanged.</summary>
    public VerifyOptions AsEffective() => Strict
        ? this with
        {
            Depth = VerifyDepth.Full,
            HashAlgorithm = FileDriftHashAlgorithm.SHA256,
            IncludeAcl = true,
        }
        : this;
}
