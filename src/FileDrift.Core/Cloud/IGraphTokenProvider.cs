// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Cloud;

/// <summary>Supplies bearer tokens for Microsoft Graph calls. An interface so the enumerator and
/// upload target can be tested without a live tenant.</summary>
public interface IGraphTokenProvider
{
    /// <summary>Returns a bearer token from the cache, refreshing it silently if needed.
    /// <para>Deliberately NEVER prompts. A verify run must not stop to open a browser — that would
    /// hang a scheduled task indefinitely. When no cached account can be refreshed this throws
    /// <see cref="GraphSignInRequiredException"/>, which callers surface as "run graph login".</para></summary>
    /// <exception cref="GraphSignInRequiredException">No usable cached credential; interactive sign-in needed.</exception>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>The cached signed-in account, or null if there is none.</summary>
    Task<GraphAccount?> GetSignedInAccountAsync(CancellationToken cancellationToken = default);
}

/// <summary>Identity of the signed-in user, for display and audit.</summary>
public sealed record GraphAccount(string Username, string? TenantId, string? Environment);
