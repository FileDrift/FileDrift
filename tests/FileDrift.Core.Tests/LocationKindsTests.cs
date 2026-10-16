// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// The location-type selector is a gate: it decides which browser opens and which paths are legal.
/// If it disagreed with the path text without saying so, the operator would get a filesystem error
/// for a SharePoint typo, which describes the symptom instead of the mistake.
/// </summary>
public class LocationKindsTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/IT", LocationKind.SharePoint)]
    [InlineData("https://contoso-my.sharepoint.com/personal/u_contoso_com/Documents", LocationKind.SharePoint)]
    [InlineData(@"\server\share", LocationKind.FileSystem)]
    [InlineData(@"D:\data", LocationKind.FileSystem)]
    [InlineData("", LocationKind.FileSystem)]
    [InlineData(null, LocationKind.FileSystem)]
    public void Detect_identifies_the_kind_a_path_looks_like(string? path, LocationKind expected) =>
        Assert.Equal(expected, LocationKinds.Detect(path));

    [Fact]
    public void A_blank_path_is_consistent_with_every_kind()
    {
        // An empty box is not yet a mistake, and flagging it would mean the form opens in an error state.
        foreach (var kind in LocationKinds.All)
        {
            Assert.True(LocationKinds.Matches(kind, ""));
            Assert.True(LocationKinds.Matches(kind, "   "));
            Assert.True(LocationKinds.Matches(kind, null));
            Assert.Null(LocationKinds.Mismatch(kind, ""));
        }
    }

    [Fact]
    public void A_filesystem_path_under_the_sharepoint_kind_is_a_mismatch()
    {
        Assert.False(LocationKinds.Matches(LocationKind.SharePoint, @"\server\share"));
        Assert.Contains("not a SharePoint URL", LocationKinds.Mismatch(LocationKind.SharePoint, @"\server\share"));
    }

    [Fact]
    public void A_sharepoint_url_under_the_filesystem_kind_is_a_mismatch()
    {
        const string url = "https://contoso.sharepoint.com/sites/IT";
        Assert.False(LocationKinds.Matches(LocationKind.FileSystem, url));
        Assert.Contains("Change the type", LocationKinds.Mismatch(LocationKind.FileSystem, url));
    }

    [Fact]
    public void Matching_paths_report_no_mismatch()
    {
        Assert.Null(LocationKinds.Mismatch(LocationKind.SharePoint, "https://contoso.sharepoint.com/sites/IT"));
        Assert.Null(LocationKinds.Mismatch(LocationKind.FileSystem, @"C:\data"));
    }

    [Fact]
    public void A_non_sharepoint_url_is_left_to_the_filesystem_to_reject()
    {
        // Guessing which schemes are "not paths" would mean maintaining that list forever; the
        // filesystem already produces a precise error for the rest.
        Assert.True(LocationKinds.Matches(LocationKind.FileSystem, "https://example.com/share"));
        Assert.False(LocationKinds.Matches(LocationKind.SharePoint, "https://example.com/share"));
    }

    [Fact]
    public void Every_kind_has_a_label_and_a_placeholder()
    {
        foreach (var kind in LocationKinds.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(LocationKinds.Label(kind)));
            Assert.False(string.IsNullOrWhiteSpace(LocationKinds.Placeholder(kind)));
        }
    }

    [Fact]
    public void A_placeholder_is_an_example_of_its_own_kind()
    {
        // A placeholder showing the wrong shape would teach the operator the wrong thing.
        foreach (var kind in LocationKinds.All)
            Assert.Equal(kind, LocationKinds.Detect(LocationKinds.Placeholder(kind).Split(" or ")[0]));
    }
}
