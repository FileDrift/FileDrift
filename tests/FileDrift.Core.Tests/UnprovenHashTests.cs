// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// A hash of null means it could not be computed, so equality was never established. Reporting such a
/// pair as Matched would be the worst failure this tool has — a certificate asserting a content match
/// at Full depth for files whose content was never compared.
/// <para>Two real ways to reach it: both files unreadable (a deny that still permits directory
/// listing), and a Full-depth cloud verify with an algorithm the service does not report, where every
/// hash comes back null.</para>
/// </summary>
public class UnprovenHashTests
{
    private static readonly ComparisonEngine Engine = new();
    private static readonly VerifyOptions FullDepth =
        new() { Depth = VerifyDepth.Full, HashAlgorithm = FileDriftHashAlgorithm.SHA256 };

    private static FileRecord Record(string path, string? hash) => new()
    {
        RelativePath = path,
        FullPath = @"X:\" + path,
        SizeBytes = 1000,            // identical sizes, so only the hash can decide
        LastWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IsDirectory = false,
        Source = EnumerationSource.Smb,
        Hash = hash,
    };

    private static ComparisonResult CompareOne(string? sourceHash, string? destHash) =>
        Assert.Single(Engine.Compare([Record("f.bin", sourceHash)], [Record("f.bin", destHash)], FullDepth));

    [Fact]
    public void Two_unhashable_files_are_NOT_reported_as_matched()
    {
        // The case that matters: equality was never established, so claiming a match would assert
        // something the run never checked.
        var result = CompareOne(null, null);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.True(result.Differences.HasFlag(FileDifference.Hash));
    }

    [Fact]
    public void One_unhashable_side_is_a_difference()
    {
        Assert.True(CompareOne("ABC123", null).Differences.HasFlag(FileDifference.Hash));
        Assert.True(CompareOne(null, "ABC123").Differences.HasFlag(FileDifference.Hash));
    }

    [Fact]
    public void Equal_hashes_still_match()
    {
        var result = CompareOne("ABC123", "ABC123");
        Assert.Equal(ComparisonStatus.Matched, result.Status);
        Assert.False(result.Differences.HasFlag(FileDifference.Hash));
    }

    [Fact]
    public void Differing_hashes_are_still_a_difference()
    {
        Assert.True(CompareOne("ABC123", "DEF456").Differences.HasFlag(FileDifference.Hash));
    }

    [Fact]
    public void Hex_comparison_remains_case_insensitive()
    {
        // Preserved behaviour: a source emitting lowercase hex must still match.
        Assert.Equal(ComparisonStatus.Matched, CompareOne("abc123", "ABC123").Status);
    }

    [Fact]
    public void QuickXor_comparison_remains_case_sensitive()
    {
        // Base64 is case-sensitive; two differing hashes must not be called equal.
        var options = new VerifyOptions
        {
            Depth = VerifyDepth.Full,
            HashAlgorithm = FileDriftHashAlgorithm.QuickXor,
        };
        var result = Assert.Single(Engine.Compare(
            [Record("f.bin", "aBc123=")], [Record("f.bin", "AbC123=")], options));

        Assert.True(result.Differences.HasFlag(FileDifference.Hash));
    }

    [Fact]
    public void Below_full_depth_a_null_hash_is_irrelevant()
    {
        // Standard depth never hashes, so an absent hash must not manufacture a difference.
        var standard = new VerifyOptions { Depth = VerifyDepth.Standard };
        var result = Assert.Single(Engine.Compare(
            [Record("f.bin", null)], [Record("f.bin", null)], standard));

        Assert.Equal(ComparisonStatus.Matched, result.Status);
    }
}
