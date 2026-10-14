// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Reporting;

/// <summary>One distinct set of explicit permissions, and where it appears.</summary>
/// <param name="Owner">Readable owner of the entries carrying this set, where available.</param>
/// <param name="Permissions">Readable explicit (non-inherited) ACEs, e.g. "Allow Administrators: Full control".</param>
/// <param name="EntryCount">How many entries carry exactly this set.</param>
/// <param name="SamplePaths">A bounded sample of those entries, relative to the scanned root.</param>
/// <param name="Sddl">The raw SDDL, kept because the readable form is a translation and an operator
/// reproducing permissions exactly may want the original.</param>
public sealed record PermissionSet(
    string? Owner,
    IReadOnlyList<string> Permissions,
    long EntryCount,
    IReadOnlyList<string> SamplePaths,
    string Sddl);

/// <summary>An inventory of the explicit permissions on a source tree.</summary>
public sealed class PermissionsInventory
{
    public required string RootPath { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required string AppVersion { get; init; }

    /// <summary>Whether files were examined, or only folders.</summary>
    public required bool IncludedFiles { get; init; }

    public long EntriesScanned { get; init; }
    public long EntriesWithExplicitPermissions { get; init; }

    /// <summary>Entries whose security descriptor could not be read. Listed rather than dropped: an
    /// inventory that silently omitted them would understate what has to be recreated.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>Distinct permission sets, most widely applied first.</summary>
    public IReadOnlyList<PermissionSet> PermissionSets { get; init; } = [];
}

/// <summary>
/// Inventories the explicit permissions on a source tree so they can be recreated elsewhere by hand.
/// </summary>
/// <remarks>
/// <para><b>This is a report, never an attestation.</b> It states what the source permissions were at
/// a point in time. It says nothing about the destination, compares nothing, and must never be
/// presented as evidence that permissions were applied correctly anywhere. FileDrift deliberately
/// cannot compare permissions across a cloud boundary — SharePoint's model has no Windows ACL
/// equivalent — and a report that blurred into an assurance would be exactly the false comfort this
/// project keeps refusing. Certificates remain the only attestation, and they cover content, not
/// permissions.</para>
/// <para><b>Grouped by distinct permission set, not listed per entry.</b> A real tree has thousands of
/// folders sharing a handful of permission sets. An operator recreating them needs to know which sets
/// exist and where they apply; a flat per-folder dump buries that.</para>
/// <para>Only EXPLICIT (non-inherited) ACEs are reported, matching how comparison treats them:
/// inherited entries are a consequence of where a folder sits and would be recreated automatically by
/// setting the parent.</para>
/// </remarks>
public static class PermissionsReport
{
    /// <summary>Paths retained per permission set. Enough to locate the set in a real tree without the
    /// report becoming the directory listing it is meant to summarise.</summary>
    public const int SamplePathLimit = 20;

    public static async Task<PermissionsInventory> BuildAsync(
        IFileEnumerator enumerator,
        string rootPath,
        string appVersion,
        bool includeFiles = false,
        AclReader? aclReader = null,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reader = aclReader ?? new AclReader();

        // Folder records are only produced when ACL mode is on, which is also what makes the scan
        // directory-aware -- the same reason the SMB scanner needs it for comparison.
        var options = new VerifyOptions
        {
            IncludeAcl = true,
            AclScope = includeFiles ? AclScope.FilesAndFolders : AclScope.FoldersOnly,
        };

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        long scanned = 0, withPermissions = 0;

        var enumProgress = new Progress<EnumerationProgress>(p =>
            progress?.Report(new VerifyProgress
            {
                Phase = VerifyPhase.EnumeratingSource,
                Processed = p.FilesFound,
                Message = p.CurrentDirectory is { } dir
                    ? $"Reading permissions in {dir}"
                    : $"Reading permissions… ({p.FilesFound:N0} entries)",
            }));

        await foreach (var record in enumerator
            .EnumerateAsync(rootPath, options, enumProgress, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!includeFiles && !record.IsDirectory) continue;
            scanned++;

            var sddl = reader.TryGetSddl(record.FullPath);
            if (sddl is null)
            {
                unreadable.Add(record.RelativePath);
                continue;
            }

            var aces = AclModel.ExplicitAces(sddl);
            if (aces.Count == 0) continue; // inherited only — nothing deliberate to recreate here
            withPermissions++;

            // Key on the explicit ACEs plus the owner, ordered, so two entries group only when their
            // deliberate permissions genuinely match.
            var ordered = aces.OrderBy(a => a, StringComparer.Ordinal).ToList();
            var owner = AclModel.Owner(sddl);
            var key = $"{owner}|{string.Join("|", ordered)}";

            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new Group
                {
                    Owner = owner,
                    Aces = ordered,
                    Sddl = sddl,
                };
            }

            group.Count++;
            if (group.Samples.Count < SamplePathLimit)
                group.Samples.Add(record.RelativePath.Length == 0 ? "." : record.RelativePath);
        }

        var sets = groups.Values
            .OrderByDescending(g => g.Count)
            .Select(g => new PermissionSet(
                g.Owner is null ? null : AclReadable.Trustee(g.Owner),
                g.Aces.Select(AclReadable.Ace).ToList(),
                g.Count,
                g.Samples,
                g.Sddl))
            .ToList();

        progress?.Report(new VerifyProgress
        {
            Phase = VerifyPhase.Done,
            Processed = scanned,
            Total = scanned,
            Message = $"{sets.Count:N0} distinct permission set(s) across {withPermissions:N0} entries",
        });

        return new PermissionsInventory
        {
            RootPath = rootPath,
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = appVersion,
            IncludedFiles = includeFiles,
            EntriesScanned = scanned,
            EntriesWithExplicitPermissions = withPermissions,
            Unreadable = unreadable,
            PermissionSets = sets,
        };
    }

    private sealed class Group
    {
        public string? Owner { get; init; }
        public required IReadOnlyList<string> Aces { get; init; }
        public required string Sddl { get; init; }
        public long Count { get; set; }
        public List<string> Samples { get; } = [];
    }
}
