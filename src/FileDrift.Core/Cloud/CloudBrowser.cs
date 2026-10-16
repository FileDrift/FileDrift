// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace FileDrift.Core.Cloud;

/// <summary>What a node in the cloud browser represents.</summary>
public enum CloudNodeKind
{
    /// <summary>A SharePoint site. Opening it lists its document libraries.</summary>
    Site,

    /// <summary>A document library (Graph calls it a drive). Opening it lists its root folders.</summary>
    Library,

    /// <summary>A folder inside a library.</summary>
    Folder,
}

/// <summary>One entry in a cloud browse listing.</summary>
/// <param name="Kind">What this node is.</param>
/// <param name="Name">Display name.</param>
/// <param name="SiteId">Graph site id, once a site is known.</param>
/// <param name="DriveId">Graph drive id, once a library is known.</param>
/// <param name="FolderPath">Forward-slash path inside the library; empty means the library root.</param>
/// <param name="WebUrl">Browser URL, and the value a selection writes into a path box.</param>
public sealed record CloudNode(
    CloudNodeKind Kind,
    string Name,
    string? SiteId,
    string? DriveId,
    string FolderPath,
    string? WebUrl)
{
    /// <summary>True when this node can hold files, and so can be chosen as a source or destination.
    /// A site cannot: files live in a library, not in a site.</summary>
    public bool IsSelectable => Kind != CloudNodeKind.Site && !string.IsNullOrEmpty(WebUrl);
}

/// <summary>
/// Browses the tenant one level at a time: sites, then libraries, then folders.
/// <para>Every listing here is bounded by what the <em>signed-in account</em> can reach. That is a
/// property worth stating rather than hiding, because an administrator will reasonably expect an
/// admin role to show everything, and under delegated access it does not: directory roles grant the
/// ability to <em>manage</em> SharePoint and to grant oneself access, not the ability to read
/// content. A site missing from this list is far more likely to be unreachable by this account than
/// absent from the tenant, so the UI says so instead of presenting the list as complete.</para>
/// </summary>
public sealed class CloudBrowser(GraphClient client)
{
    /// <summary>The caveat to display alongside any site listing. Not optional dressing: a listing
    /// presented as the tenant's contents would be a false claim.</summary>
    public const string ScopeCaveat =
        "Shows only sites this account can access. Administrator roles do not grant content access " +
        "on their own — a site you manage may still need you added to it before it appears here.";

    private readonly GraphClient _client = client;

    /// <summary>Lists sites the signed-in account can see, ordered by name.</summary>
    /// <param name="search">Graph site search text; <c>*</c> matches all indexed sites.</param>
    public async Task<IReadOnlyList<CloudNode>> ListSitesAsync(
        string search = "*", CancellationToken cancellationToken = default)
    {
        var sites = await GraphSites.ListSitesAsync(_client, search, cancellationToken).ConfigureAwait(false);

        return [.. sites
            .Where(s => !string.IsNullOrEmpty(s.WebUrl))
            .Select(s => new CloudNode(
                CloudNodeKind.Site,
                s.DisplayName ?? s.WebUrl ?? s.Id,
                s.Id, null, "", s.WebUrl))
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Lists the document libraries in a site, ordered by name.</summary>
    public async Task<IReadOnlyList<CloudNode>> ListLibrariesAsync(
        string siteId, CancellationToken cancellationToken = default)
    {
        var drives = await GraphSites.ListDrivesAsync(_client, siteId, cancellationToken).ConfigureAwait(false);

        return [.. drives
            .Select(d => new CloudNode(
                CloudNodeKind.Library,
                d.Name ?? "Documents",
                siteId, d.Id, "", d.WebUrl))
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Lists the sub-folders of a folder in a library, ordered by name.</summary>
    /// <remarks>Folders only. Files are deliberately omitted: a comparison takes a folder on each
    /// side, so listing files would offer choices the engine cannot accept.</remarks>
    public async Task<IReadOnlyList<CloudNode>> ListFoldersAsync(
        string siteId, string driveId, string folderPath, CancellationToken cancellationToken = default)
    {
        var children = await GraphSites.ListChildrenAsync(_client, driveId, folderPath, cancellationToken)
            .ConfigureAwait(false);

        return [.. children.Select(c => ToFolderNode(c, siteId, driveId, folderPath))
                           .OfType<CloudNode>()
                           .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static CloudNode? ToFolderNode(JsonNode? child, string siteId, string driveId, string parentPath)
    {
        if (child?["folder"] is null) return null;
        if (child["name"]?.GetValue<string>() is not { Length: > 0 } name) return null;

        var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath.TrimEnd('/')}/{name}";
        return new CloudNode(
            CloudNodeKind.Folder, name, siteId, driveId, path,
            child["webUrl"]?.GetValue<string>());
    }
}
