// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Cloud;
using Xunit;

namespace FileDrift.Core.Tests;

public class SharePointNamingTests
{
    [Theory]
    // Characters SharePoint rejects. NTFS rejects most of these too, so a Windows fileserver source
    // cannot contain them — but an SMB share backed by Linux or a NAS absolutely can, which is why
    // these rules are not dead code.
    [InlineData("has\"quote.txt", "has_quote.txt")]
    [InlineData("star*.txt", "star_.txt")]
    [InlineData("colon:name.txt", "colon_name.txt")]
    [InlineData("angle<brackets>.txt", "angle_brackets_.txt")]
    [InlineData("question?.txt", "question_.txt")]
    [InlineData("pipe|name.txt", "pipe_name.txt")]
    public void Invalid_characters_are_replaced(string input, string expected) =>
        Assert.Equal(expected, SharePointNaming.Sanitize(input));

    [Theory]
    [InlineData("trailing.", "trailing")]
    [InlineData("trailing...", "trailing")]
    [InlineData(" leading.txt", "leading.txt")]
    [InlineData("trailing .txt ", "trailing .txt")]
    [InlineData("name. ", "name")]   // stripping the space exposes the period, so both must go
    public void Leading_and_trailing_spaces_and_periods_are_stripped(string input, string expected) =>
        Assert.Equal(expected, SharePointNaming.Sanitize(input));

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("CON.txt", "CON_.txt")]      // reserved even with an extension
    [InlineData("nul", "nul_")]              // case-insensitive
    [InlineData("COM4.log", "COM4_.log")]
    [InlineData("desktop.ini", "desktop_.ini")]
    public void Reserved_names_are_suffixed(string input, string expected) =>
        Assert.Equal(expected, SharePointNaming.Sanitize(input));

    [Fact]
    public void Office_lock_prefix_is_neutralised()
    {
        // "~$" marks an Office owner/lock file, which SharePoint rejects.
        Assert.Equal("__lockfile.docx", SharePointNaming.Sanitize("~$lockfile.docx"));
        // A lone tilde is fine and must be left alone.
        Assert.Equal("~tilde.txt", SharePointNaming.Sanitize("~tilde.txt"));
    }

    [Fact]
    public void Path_separators_are_preserved_and_each_segment_is_sanitised()
    {
        Assert.Equal(@"ok\CON_.txt\deep_.txt",
            SharePointNaming.Sanitize(@"ok\CON.txt\deep?.txt"));
    }

    [Fact]
    public void A_name_of_only_dots_or_spaces_becomes_a_stable_placeholder()
    {
        // Must never yield an empty segment, which would corrupt the path.
        Assert.Equal("_", SharePointNaming.Sanitize("..."));
        Assert.Equal("_", SharePointNaming.Sanitize("   "));
    }

    [Fact]
    public void Sanitizing_is_idempotent()
    {
        // The upload will sanitise, and a later verify re-derives the same name. If a second pass
        // changed anything, the two would disagree and every file would look renamed again.
        foreach (var input in new[]
                 { "CON.txt", "~$x.docx", "weird?:name.txt", "trailing. ", "...", "normal.txt" })
        {
            var once = SharePointNaming.Sanitize(input);
            Assert.Equal(once, SharePointNaming.Sanitize(once));
        }
    }

    [Fact]
    public void A_legal_name_is_left_untouched()
    {
        foreach (var ok in new[] { "normal.txt", @"folder\sub\file.docx", "with spaces.xlsx", "dash-under_1.pdf" })
        {
            Assert.Equal(ok, SharePointNaming.Sanitize(ok));
            Assert.False(SharePointNaming.NeedsSanitizing(ok));
        }
    }
}

public class CloudMigrationPolicyTests
{
    private const string Dest = "https://contoso.sharepoint.com/sites/IT/Shared Documents/Target";

    [Fact]
    public void A_normal_file_is_neither_renamed_nor_blocked()
    {
        var a = new CloudMigrationPolicy(Dest).Assess(@"folder\file.txt", 1024);
        Assert.False(a.WillBeRenamed);
        Assert.False(a.IsBlocked);
    }

    [Fact]
    public void An_illegal_name_is_reported_as_a_rename_not_a_blocker()
    {
        var a = new CloudMigrationPolicy(Dest).Assess("CON.txt", 10);
        Assert.True(a.WillBeRenamed);
        Assert.False(a.IsBlocked);   // fixable by transformation, so the copy can proceed
        Assert.Equal("CON_.txt", a.SanitizedPath);
    }

    [Fact]
    public void An_over_long_path_is_a_hard_blocker()
    {
        // Cannot be sanitised away: swapping characters never shortens a path.
        var policy = new CloudMigrationPolicy(Dest, new CloudLimits { MaxPathLength = 120 });
        var deep = string.Join('\\', Enumerable.Repeat("a-folder-name", 12)) + "\\file.txt";

        var a = policy.Assess(deep, 10);
        Assert.True(a.IsBlocked);
        Assert.Contains(a.Blockers, b => b.Contains("characters, over the 120 limit"));
        // The message has to name the fix, not just the fault.
        Assert.Contains(a.Blockers, b => b.Contains("shallower destination"));
    }

    [Fact]
    public void The_library_path_counts_against_the_budget()
    {
        // The destination prefix is already spent before any source path is added; ignoring it would
        // let a migration pass preflight and then fail partway through.
        var policy = new CloudMigrationPolicy(Dest);
        Assert.True(policy.DestinationPrefixLength > 20);
        Assert.Contains(policy.Assess(new string('x', 400) + ".txt", 1).Blockers,
            b => b.Contains("over the 400 limit"));
    }

    [Fact]
    public void An_oversized_file_is_a_hard_blocker()
    {
        var policy = new CloudMigrationPolicy(Dest, new CloudLimits { MaxFileSizeBytes = 1000 });
        Assert.True(policy.Assess("big.bin", 1001).IsBlocked);
        Assert.False(policy.Assess("ok.bin", 1000).IsBlocked);  // boundary is inclusive
    }

    [Fact]
    public void Blocked_extensions_are_opt_in_and_never_guessed()
    {
        // Blocked types are tenant policy. By default FileDrift must not reject anything on that
        // basis, or it would imply a check it cannot actually perform.
        Assert.False(new CloudMigrationPolicy(Dest).Assess("tool.exe", 10).IsBlocked);

        var configured = new CloudMigrationPolicy(Dest, new CloudLimits { BlockedExtensions = ["exe", ".ps1"] });
        Assert.True(configured.Assess("tool.exe", 10).IsBlocked);
        Assert.True(configured.Assess("script.PS1", 10).IsBlocked);     // case-insensitive, dot optional
        Assert.False(configured.Assess("notes.txt", 10).IsBlocked);
    }

    [Fact]
    public void Collisions_after_sanitizing_are_detected()
    {
        // The one thing a pure transformation cannot resolve: two distinct source names mapping to one
        // destination name. Silently letting the second overwrite the first would lose data AND make
        // the verify look clean.
        var policy = new CloudMigrationPolicy(Dest);
        var report = new CloudMigrationReport();

        report.Add(policy.Assess("a?b.txt", 10), 10);
        report.Add(policy.Assess("a|b.txt", 10), 10);

        Assert.Equal(2, report.RenameCount);
        var collision = Assert.Single(report.Collisions);
        Assert.Equal("a_b.txt", collision.Sanitized);
    }

    [Fact]
    public void A_blocked_file_is_not_also_counted_as_a_collision()
    {
        // It is never going to the destination, so it cannot collide there.
        var policy = new CloudMigrationPolicy(Dest, new CloudLimits { MaxFileSizeBytes = 5 });
        var report = new CloudMigrationReport();

        report.Add(policy.Assess("a?b.txt", 100), 100);
        report.Add(policy.Assess("a|b.txt", 1), 1);

        Assert.Equal(1, report.BlockedCount);
        Assert.Empty(report.Collisions);
    }

    [Fact]
    public void The_report_bounds_its_samples_but_not_its_counts()
    {
        // Thousands of renames must not produce thousands of lines of preflight output.
        var policy = new CloudMigrationPolicy(Dest);
        var report = new CloudMigrationReport();
        for (var i = 0; i < CloudMigrationReport.SampleLimit + 40; i++)
            report.Add(policy.Assess($"name{i}?.txt", 10), 10);  // "?" is illegal, so each IS renamed

        Assert.Equal(CloudMigrationReport.SampleLimit + 40, report.RenameCount);
        Assert.Equal(CloudMigrationReport.SampleLimit, report.RenameSamples.Count);
    }

    [Fact]
    public void No_findings_means_nothing_to_report()
    {
        var report = new CloudMigrationReport();
        report.Add(new CloudMigrationPolicy(Dest).Assess("fine.txt", 10), 10);
        Assert.False(report.HasFindings);
    }
}
