// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace FileDrift.Core.Cloud;

/// <summary>
/// The naming rules a SharePoint Online / OneDrive destination imposes, and a deterministic
/// transformation that satisfies them.
/// </summary>
/// <remarks>
/// <para><b>Why sanitise rather than refuse.</b> The alternative — refusing to copy a file whose name
/// SharePoint rejects — pushes the operator into renaming files on the source file server. That means
/// altering the very data being attested, on live content, with every chance of breaking shortcuts and
/// embedded references. A documented, deterministic transformation is the better integrity position.</para>
/// <para><b>Why purity matters.</b> <see cref="Sanitize"/> is a pure function of the path alone, so a
/// later verify can re-derive the destination name instead of consulting stored state. That keeps the
/// rename map a report rather than load-bearing data — nothing to persist, migrate, or go stale.
/// Collisions are the one case purity cannot resolve (<c>a&lt;b.txt</c> and <c>a&gt;b.txt</c> both
/// become <c>a_b.txt</c>), so callers detect those separately rather than pretending otherwise.</para>
/// <para>Rules per Microsoft's documented restrictions for SharePoint Online and OneDrive.</para>
/// </remarks>
public static class SharePointNaming
{
    /// <summary>Characters SharePoint rejects outright. Forward and back slash are absent on purpose:
    /// they are path separators here, not content.</summary>
    public const string InvalidCharacters = "\"*:<>?|";

    /// <summary>The replacement for a rejected character. Underscore is itself always legal, so a
    /// sanitised name can never need sanitising again.</summary>
    public const char Replacement = '_';

    /// <summary>Names reserved by Windows that SharePoint also rejects, plus SharePoint's own.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ".lock", "desktop.ini", "_vti_",
    };

    /// <summary>True if <paramref name="relativePath"/> would be changed by <see cref="Sanitize"/>.</summary>
    public static bool NeedsSanitizing(string relativePath) =>
        !string.Equals(relativePath, Sanitize(relativePath), StringComparison.Ordinal);

    /// <summary>
    /// Returns the destination-legal form of a backslash-separated relative path. Deterministic: the
    /// same input always yields the same output, with no dependence on other paths or on call order.
    /// </summary>
    public static string Sanitize(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return relativePath;

        var segments = relativePath.Split('\\');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = SanitizeSegment(segments[i]);

        return string.Join('\\', segments);
    }

    /// <summary>Sanitises one path segment (a single file or folder name).</summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;

        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
            sb.Append(InvalidCharacters.Contains(c, StringComparison.Ordinal) ? Replacement : c);

        // A leading "~$" marks an Office lock/owner file and is rejected.
        if (sb.Length >= 2 && sb[0] == '~' && sb[1] == '$')
        {
            sb[0] = Replacement;
            sb[1] = Replacement;
        }

        // Leading and trailing spaces are rejected, as is a trailing period. Strip trailing periods
        // and spaces together, because removing one can expose the other ("name. " -> "name").
        var result = sb.ToString().TrimStart(' ').TrimEnd(' ', '.');

        // Everything was stripped (a name of only dots or spaces): keep a stable, legal placeholder
        // rather than emitting an empty segment, which would corrupt the path.
        if (result.Length == 0) return Replacement.ToString();

        // A reserved name is rejected whole and also when it carries an extension ("CON.txt"), so the
        // STEM is what must change. Appending to the end instead ("CON.txt_") leaves the stem "CON"
        // reserved -- still rejected, and not idempotent, since re-sanitizing would append again.
        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(result) || ReservedNames.Contains(stem))
        {
            var extension = Path.GetExtension(result);
            return stem + Replacement + extension;
        }

        return result;
    }
}
