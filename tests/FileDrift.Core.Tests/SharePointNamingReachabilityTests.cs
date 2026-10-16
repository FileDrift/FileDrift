// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Cloud;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Pins which rename triggers are actually reachable from a Windows source, and which are not.
/// <para>This exists because of a wasted test round. A fixture meant to exercise the illegal-character
/// rule was built with filenames containing <c>*</c> and <c>:</c> -- except NTFS cannot store those
/// characters at all, so the files that got created held the Unicode private-use substitutes a POSIX
/// shell layer maps them to. Those are legal names, FileDrift correctly left them alone, and the run
/// reported zero renames. The tool was right and the test was invalid.</para>
/// <para>The conclusion worth keeping: for a local or SMB source, the illegal-character branch is
/// effectively unreachable, and anything exercising renames has to use a reserved name, an Office lock
/// prefix, or a trailing dot or space. The branch still matters for sources that are not a Windows
/// filesystem, so it stays -- but a test that depends on it must construct the string directly.</para>
/// </summary>
public class SharePointNamingReachabilityTests
{
    [Theory]
    [InlineData("budget: draft.txt", "budget_ draft.txt")]
    [InlineData("notes*draft.txt", "notes_draft.txt")]
    public void Illegal_characters_are_replaced_when_a_name_really_contains_them(string name, string expected)
    {
        // Constructed in code on purpose: these strings cannot come from a Windows directory listing.
        Assert.True(SharePointNaming.NeedsSanitizing(name));
        Assert.Equal(expected, SharePointNaming.Sanitize(name));
    }

    [Theory]
    [InlineData('')] // what a POSIX-compatibility layer maps ':' to
    [InlineData('')] // ... and '*'
    public void Private_use_lookalikes_are_left_alone(char lookalike)
    {
        // These are legal in SharePoint and legal on NTFS. Sanitizing them would rename files that
        // did not need renaming, and the rename count is disclosed in certificates -- so inventing
        // renames would overstate how much a migration altered.
        var name = $"budget{lookalike} draft.txt";
        Assert.False(SharePointNaming.NeedsSanitizing(name));
        Assert.Equal(name, SharePointNaming.Sanitize(name));
    }

    [Theory]
    [InlineData(@"Reports\CON.txt", @"Reports\CON_.txt")]        // reserved stem
    [InlineData(@"Policies\~$budget.xlsx", @"Policies\__budget.xlsx")] // Office lock prefix
    [InlineData(@"Reports\desktop.ini", @"Reports\desktop_.ini")] // reserved whole name
    [InlineData(@"Reports\trailing.", @"Reports\trailing")]       // trailing period
    [InlineData(@"Reports\trailing ", @"Reports\trailing")]       // trailing space
    public void These_triggers_are_reachable_from_a_windows_source(string relativePath, string expected)
    {
        // Every one of these can exist on NTFS, so these are the cases a real migration test uses.
        Assert.True(SharePointNaming.NeedsSanitizing(relativePath));
        Assert.Equal(expected, SharePointNaming.Sanitize(relativePath));
    }

    [Fact]
    public void Sanitizing_stays_idempotent_across_every_reachable_trigger()
    {
        // Purity and idempotence are what let a later verify re-derive the destination name instead of
        // storing a rename map, so a second pass must be a no-op.
        foreach (var name in new[]
                 {
                     @"Reports\CON.txt", @"Policies\~$budget.xlsx", @"Reports\desktop.ini",
                     @"Reports\trailing.", @"Reports\trailing ", "budget: draft.txt",
                 })
        {
            var once = SharePointNaming.Sanitize(name);
            Assert.Equal(once, SharePointNaming.Sanitize(once));
            Assert.False(SharePointNaming.NeedsSanitizing(once));
        }
    }
}
