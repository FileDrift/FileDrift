// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Cloud;

/// <summary>A resolved SharePoint location: which library, and where inside it.</summary>
/// <param name="SiteId">Graph site id.</param>
/// <param name="SiteName">Site display name, for reports.</param>
/// <param name="DriveId">Graph drive (document library) id.</param>
/// <param name="LibraryName">Library display name, for reports.</param>
/// <param name="FolderPath">Forward-slash path of the folder inside the library; empty means the
/// library root.</param>
/// <param name="WebUrl">Browser URL of the resolved folder, for display.</param>
public sealed record GraphTarget(
    string SiteId,
    string? SiteName,
    string DriveId,
    string? LibraryName,
    string FolderPath,
    string? WebUrl);

/// <summary>
/// Turns a SharePoint URL into a <see cref="GraphTarget"/>.
/// <para>This cannot be done by parsing alone. Given
/// <c>https://host/sites/IT/Shared Documents/Payroll</c> there is no local way to know where the site
/// path ends, the library begins, and the folder path starts — a site can live at <c>/sites/IT</c> or
/// <c>/sites/IT/sub</c>, and a library's URL segment ("Shared Documents") routinely differs from its
/// Graph name ("Documents"). So resolution asks the tenant: take the longest leading path that
/// resolves as a site, then match the next segment against that site's libraries, and treat whatever
/// remains as the folder path.</para>
/// </summary>
public static class GraphTargetResolver
{
    public static async Task<GraphTarget> ResolveAsync(
        GraphClient client, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            throw new GraphAuthException($"\"{url}\" is not a valid absolute SharePoint URL.");

        // Decode each segment: a URL copied from the browser has %20 for spaces.
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var (site, consumed) = await ResolveSiteAsync(client, uri.Host, segments, cancellationToken)
            .ConfigureAwait(false);

        var remaining = segments.Skip(consumed).ToArray();
        var drives = await GraphSites.ListDrivesAsync(client, site.Id, cancellationToken).ConfigureAwait(false);

        // No path left: the site's default library root.
        if (remaining.Length == 0)
        {
            var def = await GraphSites.DefaultDriveAsync(client, site.Id, cancellationToken).ConfigureAwait(false);
            return new GraphTarget(site.Id, site.DisplayName, def.Id, def.Name, "", def.WebUrl);
        }

        var library = MatchLibrary(drives, remaining[0]);
        if (library is null)
        {
            // The next segment is not a library, so the whole remainder is a path inside the default
            // library. This is the "/sites/IT/Folder" shorthand people naturally type.
            var def = await GraphSites.DefaultDriveAsync(client, site.Id, cancellationToken).ConfigureAwait(false);
            var path = string.Join('/', remaining);
            return new GraphTarget(site.Id, site.DisplayName, def.Id, def.Name, path,
                CombineUrl(def.WebUrl, path));
        }

        var folderPath = string.Join('/', remaining.Skip(1));
        return new GraphTarget(site.Id, site.DisplayName, library.Id, library.Name, folderPath,
            CombineUrl(library.WebUrl, folderPath));
    }

    /// <summary>Finds the longest leading path that resolves as a site, so a site nested under
    /// <c>/sites/X/Y</c> is found before falling back to <c>/sites/X</c>.</summary>
    private static async Task<(GraphSites.SiteInfo Site, int SegmentsConsumed)> ResolveSiteAsync(
        GraphClient client, string host, string[] segments, CancellationToken cancellationToken)
    {
        // A site path is "/sites/Name" or "/teams/Name" (or the tenant root). Trying every prefix
        // would be a 404 storm, so only consider prefixes that could plausibly be one.
        var candidates = new List<int>();
        for (var take = Math.Min(segments.Length, 3); take >= 1; take--)
        {
            var first = segments[0];
            if (take == 1 && !IsSiteContainer(first)) continue;
            candidates.Add(take);
        }

        foreach (var take in candidates)
        {
            var path = string.Join('/', segments.Take(take));
            try
            {
                var site = await GraphSites.ResolveSiteAsync(client, $"https://{host}/{path}", cancellationToken)
                    .ConfigureAwait(false);
                return (site, take);
            }
            catch (GraphApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Not a site at this depth; try a shorter prefix.
            }
        }

        // Nothing matched: the tenant root site, with every segment belonging to the library/path.
        var root = await GraphSites.ResolveSiteAsync(client, $"https://{host}", cancellationToken)
            .ConfigureAwait(false);
        return (root, 0);
    }

    private static bool IsSiteContainer(string segment) =>
        segment.Equals("sites", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("teams", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("personal", StringComparison.OrdinalIgnoreCase);

    /// <summary>Matches a URL segment against a library. Compares the Graph name first, then the tail
    /// of the library's own web URL — necessary because the URL segment and the display name differ
    /// for the default library ("Shared Documents" in the URL, "Documents" in Graph).</summary>
    private static GraphSites.DriveInfo? MatchLibrary(IReadOnlyList<GraphSites.DriveInfo> drives, string segment)
    {
        foreach (var drive in drives)
            if (string.Equals(drive.Name, segment, StringComparison.OrdinalIgnoreCase))
                return drive;

        foreach (var drive in drives)
        {
            if (string.IsNullOrEmpty(drive.WebUrl)) continue;
            var tail = Uri.UnescapeDataString(drive.WebUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "");
            if (string.Equals(tail, segment, StringComparison.OrdinalIgnoreCase))
                return drive;
        }

        return null;
    }

    private static string? CombineUrl(string? baseUrl, string relativePath)
    {
        if (string.IsNullOrEmpty(baseUrl)) return null;
        if (string.IsNullOrEmpty(relativePath)) return baseUrl;
        var encoded = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/{encoded}";
    }
}
