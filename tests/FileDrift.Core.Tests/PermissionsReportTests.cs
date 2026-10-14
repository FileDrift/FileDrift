// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Reporting;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// The inventory's job is to make permissions recreatable by hand, so the grouping is the substance:
/// a real tree has thousands of folders sharing a handful of permission sets, and a flat per-entry
/// dump would bury that. Tested against canned SDDL; the descriptor-reading P/Invoke is only
/// meaningful against a real filesystem.
/// </summary>
public class PermissionsReportTests
{
    private const string AdminsFullControl = "O:BAG:BAD:(A;;FA;;;BA)";
    private const string UsersRead = "O:BAG:BAD:(A;;FR;;;BU)";
    private const string InheritedOnly = "O:BAG:BAD:(A;ID;FA;;;BA)";

    /// <summary>Yields a fixed set of records, so the report's own logic is what is under test.</summary>
    private sealed class FakeEnumerator(params FileRecord[] records) : IFileEnumerator
    {
        public EnumerationSource Source => EnumerationSource.Smb;
        public IReadOnlyCollection<string> InaccessiblePaths => [];

        public async IAsyncEnumerable<FileRecord> EnumerateAsync(
            string rootPath, VerifyOptions options,
            IProgress<EnumerationProgress>? progress = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var r in records) { yield return r; }
            await Task.CompletedTask;
        }
    }

    /// <summary>Returns canned SDDL per path; a path absent from the map reads as unreadable.</summary>
    private sealed class FakeAclReader(Dictionary<string, string?> byPath) : AclReader
    {
        public override string? TryGetSddl(string path) =>
            byPath.TryGetValue(path, out var sddl) ? sddl : null;
    }

    private static FileRecord Dir(string relativePath) => new()
    {
        RelativePath = relativePath,
        FullPath = @"C:\root\" + relativePath,
        SizeBytes = 0,
        LastWriteTimeUtc = DateTime.UtcNow,
        CreatedTimeUtc = DateTime.UtcNow,
        IsDirectory = true,
        Source = EnumerationSource.Smb,
    };

    private static FileRecord File(string relativePath) => Dir(relativePath) with { IsDirectory = false };

    private static Task<PermissionsInventory> Build(
        FileRecord[] records, Dictionary<string, string?> sddl, bool includeFiles = false) =>
        PermissionsReport.BuildAsync(
            new FakeEnumerator(records), @"C:\root", "test",
            includeFiles: includeFiles,
            aclReader: new FakeAclReader(sddl));

    [Fact]
    public async Task Entries_with_identical_permissions_collapse_into_one_set()
    {
        var inventory = await Build(
            [Dir("a"), Dir("b"), Dir("c")],
            new() { [@"C:\root\a"] = AdminsFullControl, [@"C:\root\b"] = AdminsFullControl, [@"C:\root\c"] = AdminsFullControl });

        var set = Assert.Single(inventory.PermissionSets);
        Assert.Equal(3, set.EntryCount);
        Assert.Equal(["a", "b", "c"], set.SamplePaths);
        Assert.Equal(3, inventory.EntriesWithExplicitPermissions);
    }

    [Fact]
    public async Task Different_permissions_stay_separate_and_are_translated()
    {
        var inventory = await Build(
            [Dir("admins"), Dir("users")],
            new() { [@"C:\root\admins"] = AdminsFullControl, [@"C:\root\users"] = UsersRead });

        Assert.Equal(2, inventory.PermissionSets.Count);
        // Readable, not raw SDDL -- the point is that an operator can act on it.
        Assert.Contains(inventory.PermissionSets, p => p.Permissions.Any(x => x.Contains("Administrators")));
        Assert.Contains(inventory.PermissionSets, p => p.Permissions.Any(x => x.Contains("Users")));
        // ...while the original is retained for anyone reproducing it exactly.
        Assert.All(inventory.PermissionSets, p => Assert.StartsWith("O:", p.Sddl));
    }

    [Fact]
    public async Task Inherited_only_entries_are_omitted()
    {
        // Inherited permissions are a consequence of where a folder sits; recreating the parent
        // recreates them. Listing them would pad the report with nothing to act on.
        var inventory = await Build(
            [Dir("explicit"), Dir("inherited")],
            new() { [@"C:\root\explicit"] = AdminsFullControl, [@"C:\root\inherited"] = InheritedOnly });

        var set = Assert.Single(inventory.PermissionSets);
        Assert.Equal(["explicit"], set.SamplePaths);
        Assert.Equal(2, inventory.EntriesScanned);
        Assert.Equal(1, inventory.EntriesWithExplicitPermissions);
    }

    [Fact]
    public async Task Unreadable_entries_are_listed_rather_than_dropped()
    {
        // Silently omitting them would understate what has to be recreated.
        var inventory = await Build(
            [Dir("ok"), Dir("denied")],
            new() { [@"C:\root\ok"] = AdminsFullControl });

        Assert.Equal(["denied"], inventory.Unreadable);
        Assert.Single(inventory.PermissionSets);
    }

    [Fact]
    public async Task Sets_are_ordered_by_how_widely_they_apply()
    {
        var inventory = await Build(
            [Dir("one"), Dir("many1"), Dir("many2")],
            new()
            {
                [@"C:\root\one"] = UsersRead,
                [@"C:\root\many1"] = AdminsFullControl,
                [@"C:\root\many2"] = AdminsFullControl,
            });

        Assert.Equal(2, inventory.PermissionSets[0].EntryCount);
        Assert.Equal(1, inventory.PermissionSets[1].EntryCount);
    }

    [Fact]
    public async Task Sample_paths_are_bounded_while_counts_are_not()
    {
        var records = Enumerable.Range(0, PermissionsReport.SamplePathLimit + 15).Select(i => Dir($"d{i}")).ToArray();
        var sddl = records.ToDictionary(r => r.FullPath, _ => (string?)AdminsFullControl);

        var inventory = await Build(records, sddl);
        var set = Assert.Single(inventory.PermissionSets);

        Assert.Equal(records.Length, set.EntryCount);
        Assert.Equal(PermissionsReport.SamplePathLimit, set.SamplePaths.Count);
    }

    [Fact]
    public async Task Files_are_skipped_unless_asked_for()
    {
        var records = new[] { Dir("folder"), File("folder/doc.txt") };
        var sddl = new Dictionary<string, string?>
        {
            [@"C:\root\folder"] = AdminsFullControl,
            [@"C:\root\folder/doc.txt"] = UsersRead,
        };

        var foldersOnly = await Build(records, sddl);
        Assert.Single(foldersOnly.PermissionSets);
        Assert.False(foldersOnly.IncludedFiles);

        var withFiles = await Build(records, sddl, includeFiles: true);
        Assert.Equal(2, withFiles.PermissionSets.Count);
        Assert.True(withFiles.IncludedFiles);
    }

    [Fact]
    public async Task The_owner_is_reported_readably()
    {
        var inventory = await Build([Dir("a")], new() { [@"C:\root\a"] = AdminsFullControl });
        Assert.Equal("Administrators", Assert.Single(inventory.PermissionSets).Owner);
    }

    [Fact]
    public async Task A_tree_with_no_explicit_permissions_yields_an_empty_report_not_a_failure()
    {
        var inventory = await Build([Dir("a")], new() { [@"C:\root\a"] = InheritedOnly });

        Assert.Empty(inventory.PermissionSets);
        Assert.Equal(0, inventory.EntriesWithExplicitPermissions);
        Assert.Equal(1, inventory.EntriesScanned);
    }
}
