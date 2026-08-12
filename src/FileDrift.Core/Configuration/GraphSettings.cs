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
    /// implemented but disabled unless <see cref="AllowChinaCloud"/> is set.</summary>
    public string Cloud { get; set; } = GraphCloud.Global;

    /// <summary>Opt-in required to select the China (21Vianet) cloud. The endpoints are implemented
    /// and correct, but off by default: FileDrift's users are predominantly US public-sector, where
    /// connecting to a cloud under another government's jurisdiction should be a deliberate,
    /// auditable choice rather than something reachable by a one-word config typo.</summary>
    public bool AllowChinaCloud { get; set; }

    /// <summary>Overrides the login authority host derived from <see cref="Cloud"/>. Normally null —
    /// an escape hatch for a cloud FileDrift doesn't know about.</summary>
    public string? AuthorityHostOverride { get; set; }

    /// <summary>Overrides the Graph base URL derived from <see cref="Cloud"/>. Normally null.</summary>
    public string? GraphBaseUrlOverride { get; set; }

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
            issues.Add($"Cloud '{Cloud}' is not recognized. Use {GraphCloud.EnabledList}, " +
                       "or set AuthorityHostOverride and GraphBaseUrlOverride explicitly.");
        else if (GraphCloud.IsChina(Cloud) && !AllowChinaCloud)
            issues.Add("Cloud 'China' (21Vianet) is disabled by default. If you genuinely operate in " +
                       "that tenant, set \"AllowChinaCloud\": true in graph.json to enable it.");

        if (PageSize is < 1 or > 999)
            issues.Add($"PageSize {PageSize} is out of range (1–999).");

        if (MaxRetries < 0)
            issues.Add($"MaxRetries {MaxRetries} cannot be negative.");

        return issues;
    }
}

/// <summary>Endpoint sets for Microsoft's national clouds.</summary>
public static class GraphCloud
{
    public const string Global = "Global";
    public const string UsGov = "UsGov";
    public const string UsGovDoD = "UsGovDoD";

    /// <summary>China (21Vianet). Fully implemented, but gated behind
    /// <see cref="GraphSettings.AllowChinaCloud"/> — see that property for why.</summary>
    public const string China = "China";

    /// <summary>Clouds selectable without an explicit opt-in. Used in error messages, so it should
    /// not advertise a cloud the user would then be told is disabled.</summary>
    public static string EnabledList => $"{Global}, {UsGov}, or {UsGovDoD}";

    /// <summary>True for any cloud FileDrift has endpoints for, including gated ones. Whether it may
    /// actually be used is a separate question — see <see cref="GraphSettings.Validate"/>.</summary>
    public static bool IsKnown(string? cloud) =>
        cloud is not null && Names.Contains(cloud.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsChina(string? cloud) =>
        string.Equals(cloud?.Trim(), China, StringComparison.OrdinalIgnoreCase);

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
