// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

/// <summary>Recognizes SharePoint Online / OneDrive for Business URLs, so they can be routed to the
/// Graph enumerator instead of being mistaken for a filesystem path.</summary>
public static class CloudPath
{
    private const string SharePointHostSuffix = ".sharepoint.com";

    /// <summary>True for an https URL whose host is a SharePoint Online tenant — this covers both
    /// team sites (<c>contoso.sharepoint.com/sites/…</c>) and personal OneDrive for Business
    /// (<c>contoso-my.sharepoint.com/personal/…</c>). Never throws on malformed input, so it is safe
    /// to call against the in-progress text of a path box.</summary>
    public static bool IsSharePoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Uri.TryCreate(path.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return uri.Host.EndsWith(SharePointHostSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when either side of a comparison lives in the cloud.</summary>
    public static bool InvolvesCloud(string? sourcePath, string? destPath) =>
        IsSharePoint(sourcePath) || IsSharePoint(destPath);

    /// <summary>Rejects cloud paths a run cannot honestly handle, so they fail with a clear message
    /// instead of being mistaken for a UNC path and producing a confusing filesystem error.</summary>
    /// <exception cref="NotSupportedException">The run involves a cloud path that isn't supported.</exception>
    public static void GuardSupported(string? sourcePath, string? destPath, bool includeAcl, bool strict)
    {
        if (!InvolvesCloud(sourcePath, destPath)) return;

        // Permanent rule, not a temporary gap: SharePoint permissions (role assignments and sharing
        // links) are a different model from Windows DACLs/SDDL, so there is nothing meaningful to
        // compare against a local ACL. Reporting "no ACL differences" would be a false assurance.
        if (includeAcl)
        {
            throw new NotSupportedException(strict
                ? "Strict mode cannot be used with SharePoint/OneDrive paths: it forces ACL comparison, " +
                  "and SharePoint permissions have no equivalent in Windows ACLs. Use Full depth without " +
                  "Strict to compare content."
                : "ACL comparison is not supported for SharePoint/OneDrive paths — SharePoint permissions " +
                  "are a different model from Windows ACLs and cannot be meaningfully compared. Turn ACL " +
                  "comparison off for this run.");
        }
    }
}
