// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Cloud;

/// <summary>Destination limits that a name transformation cannot work around.</summary>
public sealed class CloudLimits
{
    /// <summary>Maximum decoded server-relative path length SharePoint Online accepts, counting the
    /// library path plus everything below it. There is no way to sanitise a path shorter — only to
    /// shorten a name or choose a shallower destination — so an overflow is a hard stop.</summary>
    public int MaxPathLength { get; set; } = 400;

    /// <summary>Maximum single-file size (default 250 GB, SharePoint Online's documented ceiling).</summary>
    public long MaxFileSizeBytes { get; set; } = 250L * 1024 * 1024 * 1024;

    /// <summary>Extensions the destination tenant refuses, without the dot (e.g. <c>["exe","ps1"]</c>).
    /// <para>EMPTY BY DEFAULT ON PURPOSE. Blocked file types are tenant policy, not something FileDrift
    /// can discover, and a hardcoded guess would either reject files that are perfectly acceptable or
    /// imply a check it is not really performing. Populate it from your own tenant configuration.</para></summary>
    public string[] BlockedExtensions { get; set; } = [];
}

/// <summary>What a migration policy concluded about one file.</summary>
/// <param name="RelativePath">The source path, as enumerated.</param>
/// <param name="SanitizedPath">The destination-legal form; equal to the source path when no change
/// is needed.</param>
/// <param name="Blockers">Reasons the file cannot be migrated at all. Empty means it can.</param>
public sealed record CloudFileAssessment(string RelativePath, string SanitizedPath, IReadOnlyList<string> Blockers)
{
    public bool WillBeRenamed => !string.Equals(RelativePath, SanitizedPath, StringComparison.Ordinal);
    public bool IsBlocked => Blockers.Count > 0;
}

/// <summary>
/// Evaluates source files against a SharePoint destination before anything is copied: which names must
/// change, and which files cannot go at all.
/// </summary>
/// <remarks>
/// The split is deliberate. A rejected <em>name</em> is fixable by transformation and is reported as a
/// rename. A path too long, a file too large, or an extension the tenant blocks is NOT fixable that
/// way, so those files are reported as blocked — and a run that skips files must not be allowed to
/// produce a certificate that looks like a clean, complete match.
/// </remarks>
public sealed class CloudMigrationPolicy
{
    private readonly int _destinationPrefixLength;
    private readonly CloudLimits _limits;
    private readonly HashSet<string> _blockedExtensions;

    /// <param name="destinationUrl">The destination SharePoint URL, used to measure how much of the
    /// path budget the library itself already consumes. Approximated from the URL as given rather than
    /// resolved through Graph, which keeps this usable without an authenticated client; the figure is
    /// within a segment or two of the server-relative truth.</param>
    public CloudMigrationPolicy(string destinationUrl, CloudLimits? limits = null)
    {
        _limits = limits ?? new CloudLimits();
        _destinationPrefixLength = MeasurePrefix(destinationUrl);
        _blockedExtensions = new HashSet<string>(
            (_limits.BlockedExtensions ?? []).Select(e => e.TrimStart('.').Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Path characters already spent by the destination library, before any source path.</summary>
    public int DestinationPrefixLength => _destinationPrefixLength;

    public CloudFileAssessment Assess(string relativePath, long sizeBytes)
    {
        var sanitized = SharePointNaming.Sanitize(relativePath);
        var blockers = new List<string>();

        // Measure the SANITIZED path: that is what will actually exist at the destination.
        var total = _destinationPrefixLength + 1 + sanitized.Length;
        if (total > _limits.MaxPathLength)
            blockers.Add(
                $"destination path would be {total} characters, over the {_limits.MaxPathLength} limit " +
                $"(the library already uses {_destinationPrefixLength}) — shorten a folder name or " +
                "choose a shallower destination");

        if (sizeBytes > _limits.MaxFileSizeBytes)
            blockers.Add($"file is {sizeBytes:N0} bytes, over the {_limits.MaxFileSizeBytes:N0} byte limit");

        if (_blockedExtensions.Count > 0)
        {
            var ext = Path.GetExtension(sanitized).TrimStart('.');
            if (ext.Length > 0 && _blockedExtensions.Contains(ext))
                blockers.Add($"extension \"{ext}\" is in the configured blocked list for this destination");
        }

        return new CloudFileAssessment(relativePath, sanitized, blockers);
    }

    /// <summary>Length of the decoded path portion of the destination URL, e.g.
    /// <c>/sites/IT/Shared Documents</c> from a full https URL.</summary>
    private static int MeasurePrefix(string destinationUrl)
    {
        if (!Uri.TryCreate(destinationUrl?.Trim(), UriKind.Absolute, out var uri))
            return 0;
        return Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').Length;
    }
}

/// <summary>Aggregated findings for a whole source tree, with a bounded sample for reporting.</summary>
public sealed class CloudMigrationReport
{
    /// <summary>How many examples of each category to retain. A migration can produce thousands of
    /// renames; a preflight that printed them all would bury the summary that matters.</summary>
    public const int SampleLimit = 25;

    private readonly List<CloudFileAssessment> _renameSamples = [];
    private readonly List<CloudFileAssessment> _blockedSamples = [];
    private readonly Dictionary<string, string> _sanitizedToOriginal = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string First, string Second, string Sanitized)> _collisions = [];

    public long FilesAssessed { get; private set; }
    public long RenameCount { get; private set; }
    public long BlockedCount { get; private set; }
    public long BlockedBytes { get; private set; }

    public IReadOnlyList<CloudFileAssessment> RenameSamples => _renameSamples;
    public IReadOnlyList<CloudFileAssessment> BlockedSamples => _blockedSamples;

    /// <summary>Distinct source paths that sanitise to the same destination name. Purity cannot resolve
    /// these, so they are surfaced rather than silently letting one file overwrite another.</summary>
    public IReadOnlyList<(string First, string Second, string Sanitized)> Collisions => _collisions;

    public void Add(CloudFileAssessment assessment, long sizeBytes)
    {
        FilesAssessed++;

        if (assessment.IsBlocked)
        {
            BlockedCount++;
            BlockedBytes += sizeBytes;
            if (_blockedSamples.Count < SampleLimit) _blockedSamples.Add(assessment);
            // A blocked file is not going to the destination, so it cannot collide there.
            return;
        }

        if (assessment.WillBeRenamed)
        {
            RenameCount++;
            if (_renameSamples.Count < SampleLimit) _renameSamples.Add(assessment);
        }

        if (_sanitizedToOriginal.TryGetValue(assessment.SanitizedPath, out var existing))
        {
            if (!string.Equals(existing, assessment.RelativePath, StringComparison.OrdinalIgnoreCase)
                && _collisions.Count < SampleLimit)
                _collisions.Add((existing, assessment.RelativePath, assessment.SanitizedPath));
        }
        else
        {
            _sanitizedToOriginal[assessment.SanitizedPath] = assessment.RelativePath;
        }
    }

    public bool HasFindings => RenameCount > 0 || BlockedCount > 0 || _collisions.Count > 0;
}
