// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using FileDrift.Core.Engine;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Validates FileDrift's quickXorHash implementation against what Microsoft Graph actually reports,
/// by downloading a real file and hashing the same bytes locally.
/// <para>This exists because FileDrift's quickXorHash is a port of Microsoft's published algorithm,
/// verified only by self-consistency tests — deterministic, order- and length-sensitive, chunk-size
/// independent. Those prove the implementation agrees with itself, NOT that it agrees with the
/// service. Until this check passes, a Full-depth cloud verify cannot be trusted, because a wrong
/// hash would report content differences that do not exist (or, worse, miss ones that do).</para>
/// <para>It also answers a question that cannot be assumed: WHICH hash the tenant returns. SharePoint
/// Online and OneDrive for Business report quickXorHash, but other endpoints report SHA-1 or SHA-256 —
/// and if a tenant hands back SHA-256, FileDrift can compare content using the algorithm it already
/// has and quickXorHash is unnecessary.</para>
/// </summary>
public static class GraphHashCheck
{
    public sealed record Result(
        string ItemName,
        long SizeBytes,
        string? GraphQuickXor,
        string? GraphSha1,
        string? GraphSha256,
        string ComputedQuickXor,
        bool QuickXorReported,
        bool QuickXorMatches,
        string Verdict);

    /// <summary>Finds a file, downloads it, and compares hashes.</summary>
    /// <param name="drivePath">Optional drive-relative path to a specific file or folder. When null,
    /// the root of the signed-in user's OneDrive is listed and the smallest non-empty file is used —
    /// smallest because this downloads the content and the point is correctness, not throughput.</param>
    /// <param name="siteUrl">Optional SharePoint site URL. When given, that site's default document
    /// library is used instead of the signed-in user's OneDrive — necessary for an account with no
    /// OneDrive provisioned, which is common for administrative accounts.</param>
    public static async Task<Result> RunAsync(
        GraphClient client,
        string? drivePath = null,
        string? siteUrl = null,
        CancellationToken cancellationToken = default)
    {
        var item = string.IsNullOrWhiteSpace(siteUrl)
            ? await ResolveFileAsync(client, drivePath, cancellationToken).ConfigureAwait(false)
            : await ResolveSiteFileAsync(client, siteUrl!, drivePath, cancellationToken).ConfigureAwait(false);

        var name = DriveItemJson.Name(item) ?? "(unnamed)";
        var size = DriveItemJson.Size(item);
        var (quickXor, sha1, sha256) = DriveItemJson.Hashes(item);

        var downloadUrl = DriveItemJson.DownloadUrl(item)
            ?? throw new GraphAuthException(
                $"Graph did not return a download URL for \"{name}\", so its content cannot be verified.");

        await using var content = await client.DownloadAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
        var computed = QuickXorHash.ToBase64(
            await QuickXorHash.ComputeAsync(content, cancellationToken).ConfigureAwait(false));

        var reported = !string.IsNullOrWhiteSpace(quickXor);
        // Base64 is case-sensitive: compare exactly, the same way ComparisonEngine does for QuickXor.
        var matches = reported && string.Equals(quickXor, computed, StringComparison.Ordinal);

        return new Result(name, size, quickXor, sha1, sha256, computed, reported, matches, Verdict(reported, matches, sha256));
    }

    private static string Verdict(bool quickXorReported, bool matches, string? sha256) => (quickXorReported, matches) switch
    {
        (true, true) =>
            "PASS — the quickXorHash computed locally matches the one Graph reports for the same bytes. " +
            "Full-depth content verification against SharePoint/OneDrive can be trusted.",
        (true, false) =>
            "FAIL — Graph reported a quickXorHash but ours differs for the same bytes. The port is wrong. " +
            "Do NOT rely on Full-depth cloud verification until this is fixed; it would report phantom " +
            "content differences, or miss real ones.",
        (false, _) when !string.IsNullOrWhiteSpace(sha256) =>
            "INCONCLUSIVE — this tenant did not report a quickXorHash, but it did report sha256Hash. " +
            "Content comparison can use SHA-256, which FileDrift already supports, and quickXorHash is " +
            "not needed for this target.",
        _ =>
            "INCONCLUSIVE — Graph reported no quickXorHash for this item, so there is nothing to compare " +
            "against. Try a different file or library.",
    };

    /// <summary>Resolves a usable file: an explicit path, or the smallest non-empty file at the root of
    /// the signed-in user's OneDrive.</summary>
    private static async Task<JsonNode> ResolveFileAsync(
        GraphClient client, string? drivePath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(drivePath))
        {
            var encoded = Uri.EscapeDataString(drivePath.Trim().Replace('\\', '/').Trim('/'));
            // Graph's path addressing keeps '/' as a separator, so put the slashes back after escaping.
            encoded = encoded.Replace("%2F", "/", StringComparison.Ordinal);

            var resolved = await client.GetJsonAsync($"me/drive/root:/{encoded}", cancellationToken)
                                       .ConfigureAwait(false);

            if (!DriveItemJson.IsFolder(resolved)) return resolved;

            return await FirstFileAsync(
                client, $"me/drive/root:/{encoded}:/children", drivePath, cancellationToken).ConfigureAwait(false);
        }

        return await FirstFileAsync(client, "me/drive/root/children", "the OneDrive root", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Resolves a file inside a SharePoint site's default document library.</summary>
    private static async Task<JsonNode> ResolveSiteFileAsync(
        GraphClient client, string siteUrl, string? drivePath, CancellationToken cancellationToken)
    {
        var site = await GraphSites.ResolveSiteAsync(client, siteUrl, cancellationToken).ConfigureAwait(false);
        var drive = await GraphSites.DefaultDriveAsync(client, site.Id, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(drivePath))
        {
            var resolved = await GraphSites.GetItemAsync(client, drive.Id, drivePath!, cancellationToken)
                                           .ConfigureAwait(false);
            if (!DriveItemJson.IsFolder(resolved)) return resolved;
        }

        var children = await GraphSites.ListChildrenAsync(client, drive.Id, drivePath, cancellationToken)
                                       .ConfigureAwait(false);
        return Smallest(children)
            ?? throw new GraphAuthException(
                $"No non-empty file was found directly in \"{drive.Name ?? "the default library"}\" of " +
                $"{site.WebUrl ?? siteUrl}. Pass --path pointing at a file, or a folder containing one.");
    }

    /// <summary>Smallest non-empty file in a child listing, or null. Smallest because the content is
    /// downloaded and the point is correctness, not throughput.</summary>
    private static JsonNode? Smallest(JsonArray items)
    {
        JsonNode? best = null;
        foreach (var candidate in items)
        {
            if (candidate is null || DriveItemJson.IsFolder(candidate)) continue;
            if (DriveItemJson.Size(candidate) <= 0) continue;
            if (best is null || DriveItemJson.Size(candidate) < DriveItemJson.Size(best)) best = candidate;
        }
        return best;
    }

    private static async Task<JsonNode> FirstFileAsync(
        GraphClient client, string childrenUrl, string where, CancellationToken cancellationToken)
    {
        var page = await client.GetJsonAsync(childrenUrl, cancellationToken).ConfigureAwait(false);
        var items = page["value"]?.AsArray()
            ?? throw new GraphAuthException($"Graph returned no item list for {where}.");

        JsonNode? best = null;
        foreach (var candidate in items)
        {
            if (candidate is null || DriveItemJson.IsFolder(candidate)) continue;
            if (DriveItemJson.Size(candidate) <= 0) continue; // an empty file proves nothing about hashing
            if (best is null || DriveItemJson.Size(candidate) < DriveItemJson.Size(best)) best = candidate;
        }

        return best ?? throw new GraphAuthException(
            $"No non-empty file was found directly in {where}. Pass --path pointing at a file " +
            "(or a folder that contains one) to check.");
    }
}
