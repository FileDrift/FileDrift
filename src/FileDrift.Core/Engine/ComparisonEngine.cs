// SPDX-License-Identifier: GPL-3.0-or-later
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

    /// <summary>Convenience overload for plain record sets: builds the path-keyed maps and delegates.
    /// Duplicate relative paths collapse last-wins (shouldn't occur in real enumerations).</summary>
    public IReadOnlyList<ComparisonResult> Compare(
        IReadOnlyCollection<FileRecord> source,
        IReadOnlyCollection<FileRecord> dest,
        VerifyOptions options)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var srcByPath = new Dictionary<string, FileRecord>(source.Count, comparer);
        foreach (var s in source) srcByPath[s.RelativePath] = s;
        var destByPath = new Dictionary<string, FileRecord>(dest.Count, comparer);
        foreach (var d in dest) destByPath[d.RelativePath] = d;
        return Compare(srcByPath, destByPath, options);
    }

    /// <summary>Compares two record sets already keyed by relative path (as the verify pipeline holds
    /// them), avoiding any rebuild or copy of million-entry structures. Both dictionaries MUST be keyed
    /// case-insensitively (Windows path semantics). The dictionaries are not modified.</summary>
    public IReadOnlyList<ComparisonResult> Compare(
        IReadOnlyDictionary<string, FileRecord> source,
        IReadOnlyDictionary<string, FileRecord> dest,
        VerifyOptions options)
    {
        var results = new List<ComparisonResult>(source.Count);

        // Paired on the dictionary KEY, not on the record's own RelativePath. They are the same
        // thing for a like-for-like comparison, but they diverge when the destination could not
        // accept a source name: the key is then the name the file actually has at the destination,
        // while the record keeps the original for reporting. Keying off the record instead would
        // make every renamed file look missing at the destination AND extra there.
        foreach (var (key, s) in source)
        {
            if (dest.TryGetValue(key, out var d))
            {
                var diff = ComputeDifferences(s, d, options, out var aclDetail);
                results.Add(new ComparisonResult
                {
                    RelativePath = s.RelativePath,
                    // Only set when the destination name differs, so a rename is visible rather
                    // than silently resembling a like-named pair.
                    DestinationRelativePath =
                        string.Equals(s.RelativePath, d.RelativePath, StringComparison.Ordinal)
                            ? null : d.RelativePath,
                    Status = diff == FileDifference.None ? ComparisonStatus.Matched : ComparisonStatus.Different,
                    Differences = diff,
                    Source = s,
                    Dest = d,
                    AclDetail = aclDetail,
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

        // Extras: anything on dest with no source counterpart. The source map answers that directly,
        // so no matched-paths set needs to be built and carried through the first pass.
        foreach (var (key, d) in dest)
        {
            if (!source.ContainsKey(key))
                results.Add(new ComparisonResult
                {
                    RelativePath = d.RelativePath,
                    Status = ComparisonStatus.ExtraAtDest,
                    Dest = d,
                });
        }

        return results;
    }

    private FileDifference ComputeDifferences(FileRecord s, FileRecord d, VerifyOptions options, out string? aclDetail)
    {
        aclDetail = null;
        var diff = FileDifference.None;

        // Content checks apply to files only — a directory's size/timestamp/hash aren't meaningful.
        if (!s.IsDirectory)
        {
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
                // A null hash means it could not be computed, so equality was never established.
                // This is checked EXPLICITLY because string.Equals(null, null) is true: two
                // unhashable files would otherwise be reported as a content match, and a
                // certificate would assert equality at Full depth for a pair that was never
                // compared. Reachable two ways -- both files unreadable (a deny that still
                // permits directory listing), and a Full-depth cloud verify using an algorithm
                // the service does not report, where every hash comes back null.
                // quickXorHash is base64, which is CASE-SENSITIVE — comparing it case-insensitively
                // could call two genuinely different hashes equal. Hex stays case-insensitive so a
                // hash from a source that emits lowercase still matches.
                var comparison = options.HashAlgorithm == FileDriftHashAlgorithm.QuickXor
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (s.Hash is null || d.Hash is null || !string.Equals(s.Hash, d.Hash, comparison))
                    diff |= FileDifference.Hash;
            }
        }

        // Folders-only scope compares ACLs on directories only (file ACLs weren't even read).
        if (options.IncludeAcl && (options.AclScope == AclScope.FilesAndFolders || s.IsDirectory))
        {
            // Compare only explicit (non-inherited) permissions; inherited ACEs differ structurally
            // between two server roots and aren't drift.
            var aclDelta = AclModel.CompareExplicit(s.SecurityDescriptor, d.SecurityDescriptor);
            bool ownerDiffers = options.EnforceOwnership &&
                !string.Equals(AclModel.Owner(s.SecurityDescriptor), AclModel.Owner(d.SecurityDescriptor), StringComparison.OrdinalIgnoreCase);

            if (aclDelta.Any || ownerDiffers)
            {
                diff |= FileDifference.Acl;
                aclDetail = DescribeAcl(aclDelta, ownerDiffers);
            }
        }

        return diff;
    }

    private static string DescribeAcl(AclModel.AclDelta delta, bool ownerDiffers)
    {
        var parts = new List<string>();
        if (delta.DestMissing.Count > 0) parts.Add($"{delta.DestMissing.Count} missing on dest");
        if (delta.DestExtra.Count > 0) parts.Add($"{delta.DestExtra.Count} extra on dest");
        if (ownerDiffers) parts.Add("owner differs");
        return string.Join(", ", parts);
    }
}
