// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Pins the browser's listing behaviour against a canned transport: ordering, folders-only
/// filtering, nested path composition, and paging. Paging matters most -- a document library with
/// more children than one page is ordinary, and a truncated listing would silently fail to show the
/// folder the operator is looking for.
/// </summary>
public class CloudBrowserTests
{
    /// <summary>Replies to each request from a canned map, keyed by a substring of the URL.</summary>
    private sealed class CannedHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            foreach (var (key, json) in responses)
                if (url.Contains(key, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubTokens : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-token");
        public Task<GraphAccount?> GetSignedInAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GraphAccount?>(new GraphAccount("tester@example.gov", "tenant", "env"));
    }

    private static (CloudBrowser Browser, CannedHandler Handler) Build(Dictionary<string, string> responses)
    {
        var handler = new CannedHandler(responses);
        var settings = new GraphSettings
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            TenantId = "66666666-7777-8888-9999-aaaaaaaaaaaa",
        };
        return (new CloudBrowser(new GraphClient(new StubTokens(), settings, new HttpClient(handler))), handler);
    }

    private static string Value(params JsonObject[] items) =>
        new JsonObject { ["value"] = new JsonArray([.. items]) }.ToJsonString();

    private static JsonObject Folder(string name) =>
        new() { ["name"] = name, ["folder"] = new JsonObject { ["childCount"] = 0 }, ["webUrl"] = $"https://x/{name}" };

    private static JsonObject File(string name) =>
        new() { ["name"] = name, ["file"] = new JsonObject(), ["webUrl"] = $"https://x/{name}" };

    [Fact]
    public async Task Sites_are_sorted_and_entries_without_a_url_are_dropped()
    {
        // A site with no webUrl cannot be written into a path box, so offering it would be a dead end.
        var (browser, _) = Build(new()
        {
            ["sites?search"] = Value(
                new JsonObject { ["id"] = "s2", ["displayName"] = "Zoning", ["webUrl"] = "https://x/sites/Zoning" },
                new JsonObject { ["id"] = "s3", ["displayName"] = "No Url" },
                new JsonObject { ["id"] = "s1", ["displayName"] = "Accounting", ["webUrl"] = "https://x/sites/Acct" }),
        });

        var sites = await browser.ListSitesAsync();

        Assert.Equal(["Accounting", "Zoning"], sites.Select(s => s.Name));
        Assert.All(sites, s => Assert.Equal(CloudNodeKind.Site, s.Kind));
    }

    [Fact]
    public async Task A_site_is_not_selectable_because_files_live_in_a_library()
    {
        var (browser, _) = Build(new()
        {
            ["sites?search"] = Value(
                new JsonObject { ["id"] = "s1", ["displayName"] = "IT", ["webUrl"] = "https://x/sites/IT" }),
        });

        var site = Assert.Single(await browser.ListSitesAsync());
        Assert.False(site.IsSelectable);
    }

    [Fact]
    public async Task Libraries_are_sorted_and_selectable()
    {
        var (browser, _) = Build(new()
        {
            ["/drives"] = Value(
                new JsonObject { ["id"] = "d2", ["name"] = "Payroll", ["webUrl"] = "https://x/sites/IT/Payroll" },
                new JsonObject { ["id"] = "d1", ["name"] = "Documents", ["webUrl"] = "https://x/sites/IT/Shared Documents" }),
        });

        var libraries = await browser.ListLibrariesAsync("s1");

        Assert.Equal(["Documents", "Payroll"], libraries.Select(l => l.Name));
        Assert.All(libraries, l => Assert.True(l.IsSelectable));
        Assert.All(libraries, l => Assert.Equal("", l.FolderPath));
    }

    [Fact]
    public async Task Only_folders_are_listed()
    {
        // A comparison takes a folder on each side, so offering files would offer illegal choices.
        var (browser, _) = Build(new()
        {
            ["children"] = Value(Folder("Reports"), File("budget.xlsx"), Folder("Archive")),
        });

        var folders = await browser.ListFoldersAsync("s1", "d1", "");

        Assert.Equal(["Archive", "Reports"], folders.Select(f => f.Name));
        Assert.All(folders, f => Assert.Equal(CloudNodeKind.Folder, f.Kind));
    }

    [Fact]
    public async Task Folder_paths_compose_from_the_parent()
    {
        var (browser, _) = Build(new() { ["children"] = Value(Folder("2026")) });

        var nested = Assert.Single(await browser.ListFoldersAsync("s1", "d1", "Reports/Quarterly"));

        Assert.Equal("Reports/Quarterly/2026", nested.FolderPath);
        Assert.Equal("d1", nested.DriveId);
        Assert.Equal("s1", nested.SiteId);
    }

    [Fact]
    public async Task A_listing_longer_than_one_page_is_followed_to_the_end()
    {
        var (browser, handler) = Build(new()
        {
            // The nextLink is matched first, so the second page answers its own URL.
            ["page2"] = Value(Folder("Beta")),
            ["children"] = new JsonObject
            {
                ["value"] = new JsonArray(Folder("Alpha")),
                ["@odata.nextLink"] = "https://graph.example/v1.0/page2",
            }.ToJsonString(),
        });

        var folders = await browser.ListFoldersAsync("s1", "d1", "");

        Assert.Equal(["Alpha", "Beta"], folders.Select(f => f.Name));
        Assert.Contains(handler.Requested, u => u.Contains("page2"));
    }

    [Fact]
    public void The_scope_caveat_names_the_admin_role_trap()
    {
        // The listing is bounded by the signed-in account, which an administrator will not expect.
        // Presenting it as the tenant's contents would be a false claim, so the wording is pinned.
        Assert.Contains("only sites this account can access", CloudBrowser.ScopeCaveat);
        Assert.Contains("Administrator roles", CloudBrowser.ScopeCaveat);
    }
}
