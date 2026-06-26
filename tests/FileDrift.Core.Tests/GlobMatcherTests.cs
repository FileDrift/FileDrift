using FileDrift.Core.Engine;
using Xunit;

namespace FileDrift.Core.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("*.tmp", "foo.tmp", true)]
    [InlineData("*.tmp", @"a\b\x.tmp", true)]
    [InlineData("*.tmp", "foo.txt", false)]
    [InlineData("~$*", "~$doc.docx", true)]
    [InlineData(@"logs\*", @"logs\a.log", true)]
    [InlineData(@"logs\*", @"data\a.log", false)]
    [InlineData("*.TMP", "foo.tmp", true)] // case-insensitive
    public void IsExcluded_matches_patterns(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, new GlobMatcher(new[] { pattern }).IsExcluded(path));

    [Fact]
    public void Empty_patterns_exclude_nothing() =>
        Assert.False(new GlobMatcher(Array.Empty<string>()).IsExcluded("anything.tmp"));
}
