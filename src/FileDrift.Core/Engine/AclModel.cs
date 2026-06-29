// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

/// <summary>Parses SDDL security descriptors to compare the EXPLICIT (non-inherited) DACL ACEs —
/// the permissions someone deliberately set, as opposed to those inherited from the parent tree.
/// Inherited ACEs differ structurally between two server roots, so comparing them is noise.</summary>
public static class AclModel
{
    /// <summary>The explicit (non-inherited) DACL ACEs of an SDDL string, normalized for set comparison.
    /// Each entry is the ACE body "type;flags;rights;objguid;inheritguid;sid". Empty if no DACL/null.</summary>
    public static HashSet<string> ExplicitAces(string? sddl)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sddl)) return set;

        int start = sddl.IndexOf("D:", StringComparison.Ordinal); // section markers O: G: D: S:; aliases/SIDs hold no "D:"
        if (start < 0) return set;

        foreach (var ace in EnumerateAceBodies(sddl, start + 2))
            if (!HasInheritedFlag(ace))
                set.Add(ace.ToUpperInvariant());

        return set;
    }

    /// <summary>The owner SID token (SDDL "O:" component), or null.</summary>
    public static string? Owner(string? sddl) => AclReader.ExtractOwner(sddl);

    /// <summary>Difference between two explicit-ACE sets, in both directions.</summary>
    public readonly record struct AclDelta(IReadOnlyList<string> DestMissing, IReadOnlyList<string> DestExtra)
    {
        /// <summary>Explicit ACEs on the source but not the destination (additive reconcile adds these).</summary>
        public IReadOnlyList<string> DestMissing { get; } = DestMissing;
        /// <summary>Explicit ACEs on the destination but not the source (reported, never removed).</summary>
        public IReadOnlyList<string> DestExtra { get; } = DestExtra;

        public bool Any => DestMissing.Count > 0 || DestExtra.Count > 0;
    }

    public static AclDelta CompareExplicit(string? sourceSddl, string? destSddl)
    {
        var src = ExplicitAces(sourceSddl);
        var dst = ExplicitAces(destSddl);
        var missing = src.Where(a => !dst.Contains(a)).ToArray();
        var extra = dst.Where(a => !src.Contains(a)).ToArray();
        return new AclDelta(missing, extra);
    }

    private static IEnumerable<string> EnumerateAceBodies(string sddl, int from)
    {
        int i = from;
        while (i < sddl.Length)
        {
            char c = sddl[i];
            if (c == '(')
            {
                int close = sddl.IndexOf(')', i + 1);
                if (close < 0) yield break;
                yield return sddl[(i + 1)..close];
                i = close + 1;
            }
            else if (c == 'S' && i + 1 < sddl.Length && sddl[i + 1] == ':')
            {
                yield break; // SACL section begins
            }
            else
            {
                i++; // skip DACL control flags (e.g. "AI", "P") before the first ACE
            }
        }
    }

    /// <summary>True if the ACE's flags field (2nd ';'-delimited field) contains the inherited ("ID") flag.</summary>
    private static bool HasInheritedFlag(string aceBody)
    {
        int first = aceBody.IndexOf(';');
        if (first < 0) return false;
        int second = aceBody.IndexOf(';', first + 1);
        if (second < 0) return false;
        var flags = aceBody.AsSpan(first + 1, second - first - 1);
        return flags.Contains("ID", StringComparison.OrdinalIgnoreCase);
    }
}
