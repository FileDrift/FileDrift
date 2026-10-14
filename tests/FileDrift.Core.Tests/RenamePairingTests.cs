// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Cloud;
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// A destination that cannot accept a source name stores the file under a transformed one. The
/// comparison must still pair the two, or every renamed file would re-verify as missing at the
/// destination AND extra there — drift that is not drift, in the document meant to prove a migration
/// was clean.
/// </summary>
public class RenamePairingTests
{
    private static readonly ComparisonEngine Engine = new();

    private static FileRecord Record(string relativePath, long size = 100, string? hash = null) => new()
    {
        RelativePath = relativePath,
        FullPath = @"X:\" + relativePath,
        SizeBytes = size,
        LastWriteTimeUtc = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc),
        CreatedTimeUtc = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc),
        IsDirectory = false,
        Source = EnumerationSource.Smb,
        Hash = hash,
    };

    /// <summary>Builds the maps the way VerifyEngine does for a cloud destination: the source keyed
    /// under the name the destination will actually use, the record keeping its original path.</summary>
    private static (Dictionary<string, FileRecord> Source, Dictionary<string, FileRecord> Dest) Maps(
        IEnumerable<FileRecord> source, IEnumerable<FileRecord> dest, bool destinationSanitizes)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var src = new Dictionary<string, FileRecord>(cmp);
        foreach (var s in source)
            src[destinationSanitizes ? SharePointNaming.Sanitize(s.RelativePath) : s.RelativePath] = s;

        var dst = new Dictionary<string, FileRecord>(cmp);
        foreach (var d in dest) dst[d.RelativePath] = d;
        return (src, dst);
    }

    [Fact]
    public void A_renamed_file_pairs_instead_of_looking_like_drift()
    {
        // Source holds CON.txt; SharePoint stored it as CON_.txt.
        var (src, dst) = Maps([Record("CON.txt")], [Record("CON_.txt")], destinationSanitizes: true);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.Matched, result.Status);
        // Reported under the name the operator knows — the source path.
        Assert.Equal("CON.txt", result.RelativePath);
        // ...with the destination name stated explicitly rather than left to inference.
        Assert.Equal("CON_.txt", result.DestinationRelativePath);
    }

    [Fact]
    public void Without_the_mapping_the_same_pair_reads_as_missing_and_extra()
    {
        // Guards the regression this fix exists for: this is exactly what a cloud verify used to report.
        var (src, dst) = Maps([Record("CON.txt")], [Record("CON_.txt")], destinationSanitizes: false);

        var results = Engine.Compare(src, dst, new VerifyOptions());
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Status == ComparisonStatus.MissingAtDest);
        Assert.Contains(results, r => r.Status == ComparisonStatus.ExtraAtDest);
    }

    [Fact]
    public void A_like_named_pair_reports_no_destination_rename()
    {
        var (src, dst) = Maps([Record("plain.txt")], [Record("plain.txt")], destinationSanitizes: true);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.Matched, result.Status);
        Assert.Null(result.DestinationRelativePath);
    }

    [Fact]
    public void A_renamed_file_whose_content_differs_is_still_reported_as_different()
    {
        // Pairing must not paper over a real difference: the point is to compare the right two files.
        var (src, dst) = Maps(
            [Record("CON.txt", size: 100)],
            [Record("CON_.txt", size: 999)],
            destinationSanitizes: true);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.True(result.Differences.HasFlag(FileDifference.Size));
        Assert.Equal("CON_.txt", result.DestinationRelativePath);
    }

    [Fact]
    public void A_genuinely_missing_renamed_file_is_still_missing()
    {
        // Sanitizing the key must not conjure a match out of nothing.
        var (src, dst) = Maps([Record("CON.txt")], [], destinationSanitizes: true);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.MissingAtDest, result.Status);
        Assert.Equal("CON.txt", result.RelativePath);
    }

    [Fact]
    public void Nested_renamed_folders_pair_through_the_whole_path()
    {
        var (src, dst) = Maps(
            [Record(@"CON\sub\file.txt")],
            [Record(@"CON_\sub\file.txt")],
            destinationSanitizes: true);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.Matched, result.Status);
        Assert.Equal(@"CON_\sub\file.txt", result.DestinationRelativePath);
    }

    [Fact]
    public void Extras_at_the_destination_are_still_detected_under_mapping()
    {
        var (src, dst) = Maps([Record("a.txt")], [Record("a.txt"), Record("orphan.txt")],
            destinationSanitizes: true);

        var results = Engine.Compare(src, dst, new VerifyOptions());
        var extra = Assert.Single(results, r => r.Status == ComparisonStatus.ExtraAtDest);
        Assert.Equal("orphan.txt", extra.RelativePath);
    }

    [Fact]
    public void Pairing_is_unaffected_for_a_destination_that_accepts_every_name()
    {
        // Local to local: mapping is off, so a file literally named CON_.txt on both sides pairs
        // normally and nothing is transformed.
        var (src, dst) = Maps([Record("CON_.txt")], [Record("CON_.txt")], destinationSanitizes: false);

        var result = Assert.Single(Engine.Compare(src, dst, new VerifyOptions()));
        Assert.Equal(ComparisonStatus.Matched, result.Status);
        Assert.Null(result.DestinationRelativePath);
    }
}
