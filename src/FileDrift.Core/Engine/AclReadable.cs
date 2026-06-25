using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FileDrift.Core.Engine.Native;

namespace FileDrift.Core.Engine;

/// <summary>Turns raw SDDL ACE fragments (e.g. "A;;FA;;;BA") into readable text
/// ("Allow Administrators: Full control"). Static maps cover the common types/rights/well-known
/// accounts with no I/O; domain SIDs are resolved via LookupAccountSid (cached), falling back to
/// the raw SID. Windows-only for SID resolution; the static parts work anywhere.</summary>
public static class AclReadable
{
    private static readonly ConcurrentDictionary<string, string> SidCache = new();

    /// <summary>Readable form of an ACE body "type;flags;rights;objguid;inheritguid;sid".</summary>
    public static string Ace(string aceBody)
    {
        var p = aceBody.Split(';');
        if (p.Length < 6) return aceBody; // unrecognised — show raw
        var type = p[0].ToUpperInvariant() switch { "A" => "Allow", "D" => "Deny", "AU" => "Audit", _ => p[0] };
        return $"{type} {Trustee(p[5])}: {Rights(p[2])}";
    }

    /// <summary>Readable account name for an SDDL SID alias or "S-1-…" SID.</summary>
    public static string Trustee(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return "(unknown)";
        if (Aliases.TryGetValue(sid.ToUpperInvariant(), out var alias)) return alias;
        if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return SidCache.GetOrAdd(sid, ResolveSid);
        return sid;
    }

    private static string Rights(string rights)
    {
        if (string.IsNullOrEmpty(rights)) return "(none)";
        if (rights.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return HexMasks.TryGetValue(rights.ToLowerInvariant(), out var m) ? m : $"rights {rights}";

        var names = new List<string>();
        for (int i = 0; i + 1 < rights.Length; i += 2)
        {
            var tok = rights.Substring(i, 2).ToUpperInvariant();
            names.Add(RightTokens.TryGetValue(tok, out var n) ? n : tok);
        }
        return names.Count > 0 ? string.Join(", ", names) : rights;
    }

    private static string ResolveSid(string sid)
    {
        try
        {
            if (!AclMethods.ConvertStringSidToSidW(sid, out var pSid) || pSid == IntPtr.Zero)
                return sid;
            try
            {
                var name = new StringBuilder(256);
                var domain = new StringBuilder(256);
                uint cchName = 256, cchDomain = 256;
                if (AclMethods.LookupAccountSidW(null, pSid, name, ref cchName, domain, ref cchDomain, out _))
                    return domain.Length > 0 ? $"{domain}\\{name}" : name.ToString();
                return sid;
            }
            finally { AclMethods.LocalFree(pSid); }
        }
        catch { return sid; }
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BA"] = "Administrators", ["BU"] = "Users", ["BG"] = "Guests", ["PU"] = "Power Users",
        ["WD"] = "Everyone", ["SY"] = "SYSTEM", ["LS"] = "Local Service", ["NS"] = "Network Service",
        ["AU"] = "Authenticated Users", ["IU"] = "Interactive", ["NU"] = "Network", ["AN"] = "Anonymous",
        ["CO"] = "Creator Owner", ["CG"] = "Creator Group", ["DA"] = "Domain Admins", ["DU"] = "Domain Users",
        ["DG"] = "Domain Guests", ["DC"] = "Domain Computers", ["DD"] = "Domain Controllers",
        ["BO"] = "Backup Operators", ["SO"] = "Server Operators", ["AO"] = "Account Operators",
        ["PO"] = "Print Operators", ["RU"] = "Pre-Windows 2000 Access", ["LA"] = "Administrator", ["LG"] = "Guest",
        // common full well-known SIDs (avoid a lookup)
        ["S-1-1-0"] = "Everyone", ["S-1-5-18"] = "SYSTEM", ["S-1-5-32-544"] = "Administrators",
        ["S-1-5-32-545"] = "Users", ["S-1-5-11"] = "Authenticated Users", ["S-1-3-0"] = "Creator Owner",
    };

    private static readonly Dictionary<string, string> RightTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FA"] = "Full control", ["FR"] = "Read", ["FW"] = "Write", ["FX"] = "Execute",
        ["GA"] = "Full control", ["GR"] = "Read", ["GW"] = "Write", ["GX"] = "Execute",
        ["SD"] = "Delete", ["RC"] = "Read permissions", ["WD"] = "Change permissions", ["WO"] = "Take ownership",
        ["KA"] = "Full control", ["KR"] = "Read", ["KW"] = "Write", ["KX"] = "Execute",
    };

    private static readonly Dictionary<string, string> HexMasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x1f01ff"] = "Full control", ["0x1301bf"] = "Modify", ["0x1200a9"] = "Read & execute",
        ["0x120089"] = "Read", ["0x100116"] = "Write", ["0x1f0000"] = "Full control",
    };
}
