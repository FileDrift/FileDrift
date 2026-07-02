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

        foreach (var s in source.Values)
        {
            if (dest.TryGetValue(s.RelativePath, out var d))
            {
                var diff = ComputeDifferences(s, d, options, out var aclDetail);
                results.Add(new ComparisonResult
                {
                    RelativePath = s.RelativePath,
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
        foreach (var d in dest.Values)
        {
            if (!source.ContainsKey(d.RelativePath))
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
                // Null on either side means the hash could not be computed — treat as a difference
                // (conservative: we could not prove equality).
                if (!string.Equals(s.Hash, d.Hash, StringComparison.OrdinalIgnoreCase))
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
