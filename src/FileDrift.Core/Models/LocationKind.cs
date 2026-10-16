// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

/// <summary>The kind of location a path refers to.</summary>
/// <remarks>
/// This exists so the operator states the intent explicitly rather than having it inferred from
/// whatever they typed. Detection from the text alone works, but it is silent: a mistyped
/// SharePoint URL silently becomes "some filesystem path that does not exist", and the resulting
/// error describes the symptom rather than the mistake. Naming the kind up front turns that into a
/// mismatch the UI can point at.
/// </remarks>
public enum LocationKind
{
    /// <summary>A local drive or UNC share — anything the Windows filesystem can open.</summary>
    FileSystem = 0,

    /// <summary>SharePoint Online or OneDrive for Business, reached through Microsoft Graph.</summary>
    SharePoint = 1,
}

/// <summary>Helpers for <see cref="LocationKind"/>: what a path looks like, and whether it agrees
/// with the kind the operator selected.</summary>
public static class LocationKinds
{
    /// <summary>Every kind, in the order a selector should present them.</summary>
    public static readonly LocationKind[] All = [LocationKind.FileSystem, LocationKind.SharePoint];

    /// <summary>Short label for a selector.</summary>
    public static string Label(LocationKind kind) => kind switch
    {
        LocationKind.SharePoint => "SharePoint / OneDrive",
        _ => "Local or network folder",
    };

    /// <summary>Placeholder text showing the shape of a path of this kind.</summary>
    public static string Placeholder(LocationKind kind) => kind switch
    {
        LocationKind.SharePoint => "https://tenant.sharepoint.com/sites/Team/Shared Documents/Folder",
        _ => @"\server\share or D:\data",
    };

    /// <summary>The kind a path appears to be. Used to pre-select the selector when a path arrives
    /// from somewhere other than the selector itself — a saved run, a command line, a paste.</summary>
    public static LocationKind Detect(string? path) =>
        Engine.CloudPath.IsSharePoint(path) ? LocationKind.SharePoint : LocationKind.FileSystem;

    /// <summary>True when <paramref name="path"/> is consistent with <paramref name="kind"/>. Blank
    /// is consistent with everything: an empty box is not yet a mistake.</summary>
    public static bool Matches(LocationKind kind, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        return kind switch
        {
            // A trailing-slash-free https URL on a non-SharePoint host is still a mismatch, which is
            // the point: it would otherwise be treated as a relative filesystem path.
            LocationKind.SharePoint => Engine.CloudPath.IsSharePoint(path),

            // Anything that parses as a SharePoint URL is not a filesystem path. Other URLs are left
            // alone -- the filesystem will reject them with its own error, and guessing here would
            // mean maintaining a list of every scheme that is not a path.
            _ => !Engine.CloudPath.IsSharePoint(path),
        };
    }

    /// <summary>Explains a mismatch in the operator's terms, or null when there is nothing wrong.</summary>
    public static string? Mismatch(LocationKind kind, string? path)
    {
        if (Matches(kind, path)) return null;
        return kind switch
        {
            LocationKind.SharePoint =>
                "This is set to SharePoint / OneDrive, but the path is not a SharePoint URL. It should " +
                "start with https:// and point at a *.sharepoint.com address.",
            _ =>
                "This is set to a local or network folder, but the path is a SharePoint URL. Change the " +
                "type to SharePoint / OneDrive to use it.",
        };
    }
}
