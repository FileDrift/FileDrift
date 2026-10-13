// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace FileDrift.Core.Configuration;

/// <summary>Microsoft Graph (SharePoint Online / OneDrive for Business) configuration.
/// <para>Lives in <c>%APPDATA%\FileDrift\graph.json</c> rather than in the app or in source: the
/// client and tenant IDs identify one organization's Azure AD app registration, and FileDrift is a
/// general-purpose tool in a public repository. Every deployment supplies its own. Nothing here is a
/// secret — a desktop app uses delegated interactive auth, so there is no client secret to store,
/// and there should never be one in this file.</para></summary>
public sealed class GraphSettings
{
    /// <summary>Application (client) ID of the Azure AD app registration. Required.</summary>
    public string? ClientId { get; set; }

    /// <summary>Directory (tenant) ID. Required. Use the GUID for a single-tenant registration.</summary>
    public string? TenantId { get; set; }

    /// <summary>Delegated Graph scopes to request. FileDrift only reads, so the defaults are
    /// read-only; a future write/reconcile capability would need ReadWrite equivalents.</summary>
    public string[] Scopes { get; set; } = ["Files.Read.All", "Sites.Read.All"];

    /// <summary>Which Microsoft cloud the tenant lives in: <c>Global</c> (default, commercial),
    /// <c>UsGov</c> (GCC High), or <c>UsGovDoD</c>. Government tenants are not on the commercial
    /// endpoints, so this has to be settable rather than hard-coded. <c>China</c> is also
    /// implemented but restricted — see <see cref="AllowedRestrictedClouds"/>.</summary>
    public string Cloud { get; set; } = GraphCloud.Global;

    /// <summary>Clouds that are restricted by default but which this deployment explicitly permits,
    /// by name (e.g. <c>["China"]</c>). See <see cref="GraphCloud.RestrictionFor"/> for what is
    /// restricted and why. Empty by default, so a restricted cloud is never reachable without a
    /// deliberate edit to this file.</summary>
    public string[] AllowedRestrictedClouds { get; set; } = [];

    /// <summary>Overrides the login authority host derived from <see cref="Cloud"/>. Normally null —
    /// an escape hatch for a cloud FileDrift doesn't know about.</summary>
    public string? AuthorityHostOverride { get; set; }

    /// <summary>Overrides the Graph base URL derived from <see cref="Cloud"/>. Normally null.</summary>
    public string? GraphBaseUrlOverride { get; set; }

    /// <summary>How to sign in: <c>Interactive</c> (default — opens the system browser and returns to a
    /// loopback redirect) or <c>DeviceCode</c> (prints a code to enter on another device; use this on a
    /// server with no browser, or over a remote session where the loopback redirect won't work).</summary>
    public string AuthMode { get; set; } = GraphAuthModes.Interactive;

    /// <summary>Redirect URI for the interactive sign-in. <c>http://localhost</c> matches the
    /// "Public client/native" platform an app registration uses for a desktop app.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>Items requested per Graph page while enumerating. Graph caps this well below its
    /// nominal maximum for some endpoints; lower it if a large library triggers throttling.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>How many times to retry a throttled (HTTP 429) or transient 5xx Graph response.
    /// Graph throttles aggressively on large libraries, so this is expected, not exceptional.</summary>
    public int MaxRetries { get; set; } = 5;

    // The properties below are DERIVED from the settings above. They are deliberately not written to
    // graph.json: the file is meant to be hand-editable, and persisting a computed value invites
    // someone to edit it and wonder why nothing changed (or leaves it stale when Cloud changes).

    /// <summary>Login authority host for the configured cloud.</summary>
    [JsonIgnore]
    public string EffectiveAuthorityHost =>
        !string.IsNullOrWhiteSpace(AuthorityHostOverride)
            ? AuthorityHostOverride!.Trim()
            : GraphCloud.AuthorityHost(Cloud);

    /// <summary>Graph API base URL for the configured cloud.</summary>
    [JsonIgnore]
    public string EffectiveGraphBaseUrl =>
        !string.IsNullOrWhiteSpace(GraphBaseUrlOverride)
            ? GraphBaseUrlOverride!.Trim().TrimEnd('/')
            : GraphCloud.GraphBaseUrl(Cloud);

    /// <summary>The Graph resource root WITHOUT the API version segment, e.g.
    /// <c>https://graph.microsoft.com</c>. Delegated scopes must be fully qualified against this —
    /// short names like <c>Files.Read.All</c> are ambiguous across national clouds, where the resource
    /// is graph.microsoft.us or microsoftgraph.chinacloudapi.cn instead.</summary>
    [JsonIgnore]
    public string GraphResource
    {
        get
        {
            var url = EffectiveGraphBaseUrl;
            // Strip a trailing version segment ("/v1.0", "/beta") if present.
            var lastSlash = url.LastIndexOf('/');
            if (lastSlash > "https://".Length)
            {
                var tail = url[(lastSlash + 1)..];
                if (tail.Length > 0 && (tail[0] == 'v' || tail.Equals("beta", StringComparison.OrdinalIgnoreCase)))
                    return url[..lastSlash];
            }
            return url;
        }
    }

    /// <summary>Scopes fully qualified against <see cref="GraphResource"/>, as MSAL needs them. A scope
    /// already absolute (contains "://") is passed through unchanged.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> QualifiedScopes =>
        (Scopes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Contains("://", StringComparison.Ordinal) ? s.Trim() : $"{GraphResource}/{s.Trim()}")
            .ToList();

    /// <summary>Full MSAL authority (authority host + tenant), or null if the tenant isn't set.</summary>
    [JsonIgnore]
    public string? Authority =>
        string.IsNullOrWhiteSpace(TenantId) ? null : $"{EffectiveAuthorityHost}/{TenantId.Trim()}";

    /// <summary>True when the minimum required values are present.</summary>
    [JsonIgnore]
    public bool IsConfigured => Validate().Count == 0;

    /// <summary>Returns human-readable reasons the configuration is unusable; empty when it's valid.
    /// Callers surface these directly, so they name the fix, not just the problem.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(ClientId))
            issues.Add("ClientId is not set (the Application (client) ID from your Azure AD app registration).");
        else if (!Guid.TryParse(ClientId.Trim(), out _))
            issues.Add($"ClientId '{ClientId}' is not a GUID.");

        if (string.IsNullOrWhiteSpace(TenantId))
            issues.Add("TenantId is not set (the Directory (tenant) ID from your Azure AD app registration).");
        else if (!Guid.TryParse(TenantId.Trim(), out _)
                 && !TenantId.Trim().Contains('.', StringComparison.Ordinal)) // also allow contoso.onmicrosoft.com
            issues.Add($"TenantId '{TenantId}' is neither a GUID nor a domain name.");

        if (Scopes is null || Scopes.Length == 0)
            issues.Add("Scopes is empty — at least one delegated Graph scope is required.");

        if (!GraphCloud.IsKnown(Cloud) && string.IsNullOrWhiteSpace(AuthorityHostOverride))
            issues.Add($"Cloud '{Cloud}' is not recognized. Use {GraphCloud.UnrestrictedList}, " +
                       "or set AuthorityHostOverride and GraphBaseUrlOverride explicitly.");
        else if (GraphCloud.RestrictionFor(Cloud) is { } restriction && !PermitsRestrictedCloud(Cloud))
            issues.Add($"Cloud '{restriction.Name}' is restricted by default: {restriction.Reason} " +
                       $"If this deployment genuinely operates there, add \"{restriction.Name}\" to " +
                       "AllowedRestrictedClouds in graph.json.");

        if (!GraphAuthModes.IsKnown(AuthMode))
            issues.Add($"AuthMode '{AuthMode}' is not recognized. Use {GraphAuthModes.KnownList}.");

        if (PageSize is < 1 or > 999)
            issues.Add($"PageSize {PageSize} is out of range (1–999).");

        if (MaxRetries < 0)
            issues.Add($"MaxRetries {MaxRetries} cannot be negative.");

        return issues;
    }

    /// <summary>True when <paramref name="cloud"/> appears in <see cref="AllowedRestrictedClouds"/>.</summary>
    public bool PermitsRestrictedCloud(string? cloud) =>
        cloud is not null &&
        AllowedRestrictedClouds is not null &&
        AllowedRestrictedClouds.Contains(cloud.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Endpoint sets for Microsoft's national clouds. Which of them require an opt-in is
/// decided by <see cref="CloudRestrictions"/>, which is provider-neutral so the same policy will
/// cover AWS regions and other S3-compatible endpoints when those targets land.</summary>
public static class GraphCloud
{
    public const string Global = "Global";
    public const string UsGov = "UsGov";
    public const string UsGovDoD = "UsGovDoD";

    /// <summary>China (21Vianet). Fully implemented, but restricted — see
    /// <see cref="RestrictionFor"/>.</summary>
    public const string China = "China";

    /// <summary>Clouds selectable without an opt-in. Used in error messages, so it deliberately does
    /// not advertise a cloud the user would then be told is restricted.</summary>
    public static string UnrestrictedList => $"{Global}, {UsGov}, or {UsGovDoD}";

    /// <summary>Returns the restriction on <paramref name="cloud"/>, or null if it is unrestricted.
    /// Unknown clouds return null — they are rejected separately, by name.</summary>
    public static CloudRestriction? RestrictionFor(string? cloud) =>
        IsKnown(cloud) ? CloudRestrictions.ForMicrosoftCloud(cloud) : null;

    /// <summary>True for any cloud FileDrift has endpoints for, including restricted ones. Whether it
    /// may actually be used is a separate question — see <see cref="GraphSettings.Validate"/>.</summary>
    public static bool IsKnown(string? cloud) =>
        cloud is not null && Names.Contains(cloud.Trim(), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Names = [Global, UsGov, UsGovDoD, China];

    public static string AuthorityHost(string? cloud) => Normalize(cloud) switch
    {
        UsGov or UsGovDoD => "https://login.microsoftonline.us",
        China => "https://login.chinacloudapi.cn",
        _ => "https://login.microsoftonline.com",
    };

    public static string GraphBaseUrl(string? cloud) => Normalize(cloud) switch
    {
        UsGov => "https://graph.microsoft.us/v1.0",
        UsGovDoD => "https://dod-graph.microsoft.us/v1.0",
        China => "https://microsoftgraph.chinacloudapi.cn/v1.0",
        _ => "https://graph.microsoft.com/v1.0",
    };

    private static string Normalize(string? cloud)
    {
        var trimmed = cloud?.Trim();
        foreach (var name in Names)
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                return name;
        return Global;
    }
}

/// <summary>How FileDrift signs in to Microsoft Graph.</summary>
public static class GraphAuthModes
{
    /// <summary>System browser + loopback redirect. Default; best on a desktop.</summary>
    public const string Interactive = "Interactive";

    /// <summary>Print a code to enter at microsoft.com/devicelogin on another device. For a server
    /// with no browser, or a remote session where the loopback redirect cannot be reached.</summary>
    public const string DeviceCode = "DeviceCode";

    public static string KnownList => $"{Interactive} or {DeviceCode}";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode?.Trim(), Interactive, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode?.Trim(), DeviceCode, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeviceCode(string? mode) =>
        string.Equals(mode?.Trim(), DeviceCode, StringComparison.OrdinalIgnoreCase);
}
