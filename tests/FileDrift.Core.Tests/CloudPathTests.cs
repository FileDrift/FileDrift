// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

public class CloudPathTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/IT/Shared Documents")]
    [InlineData("https://contoso.sharepoint.com/sites/IT")]
    [InlineData("https://contoso-my.sharepoint.com/personal/user_contoso_com/Documents")] // OneDrive
    [InlineData("HTTPS://CONTOSO.SHAREPOINT.COM/sites/IT")]                               // case-insensitive host
    public void Recognizes_sharepoint_and_onedrive_urls(string path) =>
        Assert.True(CloudPath.IsSharePoint(path));

    [Theory]
    [InlineData(@"\\server\share")]                       // UNC
    [InlineData(@"C:\Data")]                              // local
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"\\sharepoint.com\share")]               // UNC that merely mentions the host
    [InlineData("https://contoso.example.com/sites/IT")]  // https but not SharePoint
    [InlineData("http://contoso.sharepoint.com/sites/IT")] // http, not https
    [InlineData("https://notsharepoint.com/x")]
    [InlineData("https://evil-sharepoint.com.attacker.net/x")] // suffix appears but isn't the host ending
    public void Rejects_non_sharepoint_paths(string? path) =>
        Assert.False(CloudPath.IsSharePoint(path));

    [Fact]
    public void Does_not_throw_on_partial_input_being_typed()
    {
        // These fire on every keystroke in the GUI path box, so they must never throw.
        foreach (var partial in new[] { "h", "https:", "https://", "https://con", @"\\", "C:" })
            _ = CloudPath.IsSharePoint(partial);
    }

    [Fact]
    public void PredictSource_routes_sharepoint_to_graph()
    {
        Assert.Equal(EnumerationSource.Graph,
            SmartFileEnumerator.PredictSource("https://contoso.sharepoint.com/sites/IT"));
        Assert.Equal(EnumerationSource.Smb,
            SmartFileEnumerator.PredictSource(@"\\server\share"));
    }

    [Fact]
    public void Guard_allows_purely_local_runs()
    {
        // No cloud path involved: ACL comparison and Strict stay perfectly legal.
        CloudPath.GuardSupported(@"\\server\share", @"C:\Dest", includeAcl: true, strict: true);
    }

    [Fact]
    public void Guard_rejects_acl_comparison_against_cloud()
    {
        var ex = Assert.Throws<NotSupportedException>(() => CloudPath.GuardSupported(
            "https://contoso.sharepoint.com/sites/IT", @"C:\Dest", includeAcl: true, strict: false));
        Assert.Contains("ACL comparison is not supported", ex.Message);
    }

    [Fact]
    public void Guard_calls_out_strict_specifically_since_strict_forces_acls()
    {
        var ex = Assert.Throws<NotSupportedException>(() => CloudPath.GuardSupported(
            "https://contoso.sharepoint.com/sites/IT", @"C:\Dest", includeAcl: true, strict: true));
        Assert.Contains("Strict mode", ex.Message);
    }

    [Fact]
    public void Guard_allows_a_cloud_path_when_acl_comparison_is_off()
    {
        // The enumerator now exists, so a cloud path is a legitimate target. Only the ACL rule
        // (permanent) and the hard destination limits still block anything.
        CloudPath.GuardSupported(
            @"C:\Source", "https://contoso.sharepoint.com/sites/IT", includeAcl: false, strict: false);
        CloudPath.GuardSupported(
            "https://contoso.sharepoint.com/sites/IT", @"C:\Dest", includeAcl: false, strict: false);
    }
}
