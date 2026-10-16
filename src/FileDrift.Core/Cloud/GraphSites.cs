// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Resolves SharePoint sites and document libraries (Graph calls a library a "drive").
/// <para>A SharePoint URL cannot be split into site and library by string parsing alone: given
/// <c>/sites/IT/Shared Documents/Payroll</c> there is no way to know locally where the site path ends
/// and the library begins — only the tenant knows. So resolution asks Graph, longest site path first,
/// then matches the next segment against the site's libraries.</para>
/// </summary>
public static class GraphSites
{
    public sealed record SiteInfo(string Id, string? DisplayName, string? WebUrl);
    public sealed record DriveInfo(string Id, string? Name, string? WebUrl, string? DriveType);

    /// <summary>Lists SharePoint sites the signed-in account can see. <paramref name="search"/> of
    /// <c>*</c> matches all indexed sites.</summary>
    public static async Task<IReadOnlyList<SiteInfo>> ListSitesAsync(
        GraphClient client, string search = "*", CancellationToken cancellationToken = default)
    {
        var url = $"sites?search={Uri.EscapeDataString(search)}";
        var results = new List<SiteInfo>();

        while (true)
        {
            var page = await client.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (page["value"]?.AsArray() is { } items)
            {
                foreach (var site in items)
                {
                    if (site?["id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                    results.Add(new SiteInfo(
                        id,
                        site["displayName"]?.GetValue<string>() ?? site["name"]?.GetValue<string>(),
                        site["webUrl"]?.GetValue<string>()));
                }
            }

            if (page["@odata.nextLink"]?.GetValue<string>() is not { Length: > 0 } next) break;
            url = next;
        }

        return results;
    }

    /// <summary>Lists the document libraries in a site.</summary>
    public static async Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(
        GraphClient client, string siteId, CancellationToken cancellationToken = default)
    {
        var page = await client.GetJsonAsync($"sites/{siteId}/drives", cancellationToken).ConfigureAwait(false);
        var results = new List<DriveInfo>();

        if (page["value"]?.AsArray() is { } items)
        {
            foreach (var drive in items)
            {
                if (drive?["id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                results.Add(new DriveInfo(
                    id,
                    drive["name"]?.GetValue<string>(),
                    drive["webUrl"]?.GetValue<string>(),
                    drive["driveType"]?.GetValue<string>()));
            }
        }

        return results;
    }

    /// <summary>Resolves a site from its URL, e.g. <c>https://contoso.sharepoint.com/sites/IT</c>.</summary>
    public static async Task<SiteInfo> ResolveSiteAsync(
        GraphClient client, string siteUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(siteUrl?.Trim(), UriKind.Absolute, out var uri))
            throw new GraphAuthException($"\"{siteUrl}\" is not a valid absolute site URL.");

        var relative = uri.AbsolutePath.Trim('/');
        var address = relative.Length == 0
            ? $"sites/{uri.Host}"
            : $"sites/{uri.Host}:/{relative}";

        var site = await client.GetJsonAsync(address, cancellationToken).ConfigureAwait(false);
        var id = site["id"]?.GetValue<string>()
            ?? throw new GraphAuthException($"Graph returned no site id for \"{siteUrl}\".");

        return new SiteInfo(
            id,
            site["displayName"]?.GetValue<string>() ?? site["name"]?.GetValue<string>(),
            site["webUrl"]?.GetValue<string>());
    }

    /// <summary>Returns the site's default document library.</summary>
    public static async Task<DriveInfo> DefaultDriveAsync(
        GraphClient client, string siteId, CancellationToken cancellationToken = default)
    {
        var drive = await client.GetJsonAsync($"sites/{siteId}/drive", cancellationToken).ConfigureAwait(false);
        var id = drive["id"]?.GetValue<string>()
            ?? throw new GraphAuthException("Graph returned no default document library for this site.");

        return new DriveInfo(
            id,
            drive["name"]?.GetValue<string>(),
            drive["webUrl"]?.GetValue<string>(),
            drive["driveType"]?.GetValue<string>());
    }

    /// <summary>Lists the immediate children of a path within a drive. An empty or null
    /// <paramref name="itemPath"/> lists the drive root.</summary>
    /// <remarks>Follows <c>@odata.nextLink</c> to completion. A folder with more children than the
    /// page size is ordinary in a document library, and a silently truncated listing would be worse
    /// than a slow one -- a browser would simply not show the folder you wanted.</remarks>
    public static async Task<JsonArray> ListChildrenAsync(
        GraphClient client, string driveId, string? itemPath, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(itemPath)
            ? $"drives/{driveId}/root/children"
            : $"drives/{driveId}/root:/{EncodePath(itemPath)}:/children";

        var all = new JsonArray();
        while (true)
        {
            var page = await client.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (page["value"]?.AsArray() is { } items)
            {
                // Detach each node before re-parenting it: a JsonNode belongs to one parent.
                foreach (var item in items.ToArray())
                {
                    items.Remove(item);
                    all.Add(item);
                }
            }

            if (page["@odata.nextLink"]?.GetValue<string>() is not { Length: > 0 } next) break;
            url = next;
        }

        return all;
    }

    /// <summary>Fetches a single item by path within a drive.</summary>
    public static async Task<JsonNode> GetItemAsync(
        GraphClient client, string driveId, string itemPath, CancellationToken cancellationToken = default)
        => await client.GetJsonAsync($"drives/{driveId}/root:/{EncodePath(itemPath)}", cancellationToken)
                       .ConfigureAwait(false);

    /// <summary>Percent-encodes a path for Graph's <c>root:/path</c> addressing while preserving the
    /// separators. Graph wants forward slashes, so a Windows-style path is translated.</summary>
    public static string EncodePath(string itemPath)
    {
        var normalized = itemPath.Replace('\\', '/').Trim('/');
        return string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString));
    }
}
