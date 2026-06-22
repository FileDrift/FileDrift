using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Diffs two sets of <see cref="FileRecord"/> (source vs. destination) keyed by relative path.</summary>
public sealed class ComparisonEngine
{
    /// <summary>Timestamps within this tolerance are treated as equal. Defaults to 2s to absorb
    /// FAT/exFAT 2-second granularity and sub-second rounding across filesystems.</summary>
    public static readonly TimeSpan DefaultTimestampTolerance = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _timestampTolerance;

    public ComparisonEngine(TimeSpan? timestampTolerance = null) =>
        _timestampTolerance = timestampTolerance ?? DefaultTimestampTolerance;

    /// <summary>Compares two record sets. Paths are matched case-insensitively (Windows semantics).</summary>
    public IReadOnlyList<ComparisonResult> Compare(
        IReadOnlyCollection<FileRecord> source,
        IReadOnlyCollection<FileRecord> dest,
        VerifyOptions options)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var destByPath = new Dictionary<string, FileRecord>(comparer);
        foreach (var d in dest)
            destByPath[d.RelativePath] = d; // last-wins on dup paths (shouldn't occur)

        var results = new List<ComparisonResult>(source.Count);
        var matchedDestPaths = new HashSet<string>(comparer);

        foreach (var s in source)
        {
            if (destByPath.TryGetValue(s.RelativePath, out var d))
            {
                matchedDestPaths.Add(s.RelativePath);
                var diff = ComputeDifferences(s, d, options);
                results.Add(new ComparisonResult
                {
                    RelativePath = s.RelativePath,
                    Status = diff == FileDifference.None ? ComparisonStatus.Matched : ComparisonStatus.Different,
                    Differences = diff,
                    Source = s,
                    Dest = d,
                });
            }
            else
            {
                results.Add(new ComparisonResult
                {
                    RelativePath = s.RelativePath,
                    Status = ComparisonStatus.MissingAtDest,
                    Source = s,
                });
            }
        }

        foreach (var d in dest)
        {
            if (!matchedDestPaths.Contains(d.RelativePath))
                results.Add(new ComparisonResult
                {
                    RelativePath = d.RelativePath,
                    Status = ComparisonStatus.ExtraAtDest,
                    Dest = d,
                });
        }

        return results;
    }

    private FileDifference ComputeDifferences(FileRecord s, FileRecord d, VerifyOptions options)
    {
        var diff = FileDifference.None;

        if (s.SizeBytes != d.SizeBytes)
            diff |= FileDifference.Size;

        if (options.Depth >= VerifyDepth.Standard)
        {
            var delta = (s.LastWriteTimeUtc - d.LastWriteTimeUtc).Duration();
            if (delta > _timestampTolerance)
                diff |= FileDifference.Timestamp;
        }

        if (options.Depth >= VerifyDepth.Full)
        {
            // Null on either side means the hash could not be computed — treat as a difference
            // (conservative: we could not prove equality).
            if (!string.Equals(s.Hash, d.Hash, StringComparison.OrdinalIgnoreCase))
                diff |= FileDifference.Hash;
        }

        if (options.IncludeAcl)
        {
            if (!string.Equals(s.SecurityDescriptor, d.SecurityDescriptor, StringComparison.Ordinal))
                diff |= FileDifference.Acl;
        }

        return diff;
    }
}
