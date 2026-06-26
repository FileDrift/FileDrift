using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

public class ComparisonEngineTests
{
    [Fact]
    public void Classifies_matched_different_missing_extra()
    {
        var src = new[] { Rec.File("same.txt", 10), Rec.File("diff.txt", 10), Rec.File("only-src.txt", 5) };
        var dst = new[] { Rec.File("same.txt", 10), Rec.File("diff.txt", 20), Rec.File("only-dst.txt", 5) };

        var results = new ComparisonEngine().Compare(src, dst, new VerifyOptions());

        Assert.Equal(ComparisonStatus.Matched, results.Single(r => r.RelativePath == "same.txt").Status);
        var diff = results.Single(r => r.RelativePath == "diff.txt");
        Assert.Equal(ComparisonStatus.Different, diff.Status);
        Assert.True(diff.Differences.HasFlag(FileDifference.Size));
        Assert.Equal(ComparisonStatus.MissingAtDest, results.Single(r => r.RelativePath == "only-src.txt").Status);
        Assert.Equal(ComparisonStatus.ExtraAtDest, results.Single(r => r.RelativePath == "only-dst.txt").Status);
    }

    [Fact]
    public void Timestamp_within_default_tolerance_is_matched()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var results = new ComparisonEngine().Compare(
            new[] { Rec.File("a.txt", 10, t) },
            new[] { Rec.File("a.txt", 10, t.AddSeconds(1)) }, // within 2s default
            new VerifyOptions());
        Assert.Equal(ComparisonStatus.Matched, results.Single().Status);
    }

    [Fact]
    public void Strict_zero_tolerance_flags_subsecond_timestamp()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var results = new ComparisonEngine(TimeSpan.Zero).Compare(
            new[] { Rec.File("a.txt", 10, t) },
            new[] { Rec.File("a.txt", 10, t.AddSeconds(1)) },
            new VerifyOptions());
        Assert.True(results.Single().Differences.HasFlag(FileDifference.Timestamp));
    }
}
