// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace FileDrift.Core.Engine;

/// <summary>Matches relative paths against simple wildcard globs (<c>*</c> and <c>?</c>).
/// A pattern is tested against both the full relative path and the file name, so
/// <c>*.tmp</c> excludes <c>a\b\x.tmp</c> and <c>logs\*</c> excludes everything under logs.</summary>
public sealed class GlobMatcher
{
    private readonly Regex[] _patterns;

    public GlobMatcher(IReadOnlyList<string> patterns)
    {
        _patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ToRegex(p.Trim()))
            .ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool IsExcluded(string relativePath)
    {
        if (_patterns.Length == 0) return false;

        var name = Path.GetFileName(relativePath);
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(relativePath) || pattern.IsMatch(name))
                return true;
        }
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var escaped = Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
