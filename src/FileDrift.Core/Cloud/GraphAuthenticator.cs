// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Configuration;
using FileDrift.Core.Persistence;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Signs in to Microsoft Graph as the current user (delegated auth) and supplies bearer tokens.
/// <para>Delegated, not application, auth: FileDrift acts as the signed-in person, so it can only ever
/// read or write what that account already could. There is no client secret — this is a public client,
/// and a secret in a general-purpose tool's configuration would be both unnecessary and wrong.</para>
/// </summary>
public sealed class GraphAuthenticator : IGraphTokenProvider
{
    private readonly GraphSettings _settings;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPublicClientApplication? _app;

    /// <param name="settings">Validated Graph configuration.</param>
    /// <param name="cacheDirectory">Where to keep the token cache; defaults to the FileDrift data
    /// directory. Overridable so tests never touch the real profile.</param>
    public GraphAuthenticator(GraphSettings settings, string? cacheDirectory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cacheDirectory = cacheDirectory ?? AppPaths.DataDirectory;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account is null) throw new GraphSignInRequiredException();

        try
        {
            var result = await app.AcquireTokenSilent(_settings.QualifiedScopes, account)
                                  .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            // Refresh token expired or revoked, or the requested scopes changed since last consent.
            throw new GraphSignInRequiredException(ex.Message);
        }
        catch (MsalException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<GraphAccount?> GetSignedInAccountAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        return account is null
            ? null
            : new GraphAccount(account.Username, account.HomeAccountId?.TenantId, account.Environment);
    }

    /// <summary>Performs an explicit, user-initiated sign-in. This is the ONLY path that may prompt —
    /// token acquisition during a run never does.</summary>
    /// <param name="deviceCodePrompt">Receives the device-code instructions to display. Required when
    /// <see cref="GraphSettings.AuthMode"/> is DeviceCode.</param>
    public async Task<GraphAccount> SignInAsync(
        Action<string>? deviceCodePrompt = null,
        CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AuthenticationResult result;
            if (GraphAuthModes.IsDeviceCode(_settings.AuthMode))
            {
                if (deviceCodePrompt is null)
                    throw new GraphAuthException(
                        "AuthMode is DeviceCode but no way to display the code was provided.");

                result = await app.AcquireTokenWithDeviceCode(
                        _settings.QualifiedScopes,
                        info => { deviceCodePrompt(info.Message); return Task.CompletedTask; })
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await app.AcquireTokenInteractive(_settings.QualifiedScopes)
                                  .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }

            return new GraphAccount(
                result.Account.Username,
                result.Account.HomeAccountId?.TenantId,
                result.Account.Environment);
        }
        catch (MsalException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>Removes every cached account, so the next run requires a fresh sign-in. Returns the
    /// number of accounts removed.</summary>
    public async Task<int> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToList();
        foreach (var account in accounts)
            await app.RemoveAsync(account).ConfigureAwait(false);
        return accounts.Count;
    }

    private async Task<IPublicClientApplication> GetAppAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) return _app;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null) return _app;

            var issues = _settings.Validate();
            if (issues.Count > 0)
                throw new GraphAuthException(
                    "Graph is not configured correctly: " + string.Join(" ", issues) +
                    " Run \"FileDrift-CLI graph show\" to review, or \"graph set\" to fix.");

            var app = PublicClientApplicationBuilder
                .Create(_settings.ClientId!.Trim())
                .WithAuthority(_settings.Authority!)
                .WithRedirectUri(_settings.RedirectUri)
                .Build();

            // Persist the cache so sign-in survives process exit. On Windows MSAL encrypts it with
            // DPAPI for the current user. No unprotected-file fallback is configured on purpose: the
            // cache holds refresh tokens, and failing loudly on an unsupported platform is correct
            // where silently writing credentials in plaintext would not be.
            var storage = new StorageCreationPropertiesBuilder(
                    AppPaths.GraphTokenCacheFileName, _cacheDirectory)
                .Build();
            var helper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            helper.RegisterCache(app.UserTokenCache);

            _app = app;
            return app;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Turns MSAL/AAD failures into something actionable. The raw messages name an AADSTS code
    /// and little else; these say what to actually change.</summary>
    private GraphAuthException Translate(MsalException ex)
    {
        var raw = ex.Message ?? "";

        // "Allow public client flows" left disabled on the app registration — the most likely
        // misconfiguration for a desktop app, and the raw error (demanding a client_secret) sends
        // people down the wrong path of creating one.
        if (raw.Contains("AADSTS7000218", StringComparison.Ordinal) ||
            raw.Contains("client_assertion", StringComparison.Ordinal))
            return new GraphAuthException(
                "The app registration is rejecting public-client sign-in. In Entra ID, under App " +
                $"registrations > (client {_settings.ClientId}) > Authentication, set \"Allow public " +
                "client flows\" to Yes. Do NOT create a client secret — a desktop app should not use one.", ex);

        if (raw.Contains("AADSTS65001", StringComparison.Ordinal))
            return new GraphAuthException(
                "Consent has not been granted for the requested permissions " +
                $"({string.Join(", ", _settings.Scopes ?? [])}). An administrator needs to grant admin " +
                "consent for them under App registrations > API permissions.", ex);

        if (raw.Contains("AADSTS700016", StringComparison.Ordinal))
            return new GraphAuthException(
                $"The tenant does not recognize client ID {_settings.ClientId}. Check that the client and " +
                $"tenant IDs come from the same app registration (tenant {_settings.TenantId}).", ex);

        if (raw.Contains("AADSTS50020", StringComparison.Ordinal))
            return new GraphAuthException(
                "The account you signed in with belongs to a different tenant than the one configured " +
                $"({_settings.TenantId}). Sign in with an account in that tenant.", ex);

        if (raw.Contains("AADSTS50011", StringComparison.Ordinal))
            return new GraphAuthException(
                $"Redirect URI mismatch. The app registration must list \"{_settings.RedirectUri}\" under " +
                "Authentication > Platform configurations > Mobile and desktop applications.", ex);

        return new GraphAuthException(
            $"Microsoft Graph sign-in failed: {raw}" +
            (GraphAuthModes.IsDeviceCode(_settings.AuthMode)
                ? ""
                : " If this machine has no usable browser (a server, or a remote session where the " +
                  "loopback redirect cannot be reached), set \"AuthMode\": \"DeviceCode\" in graph.json."), ex);
    }
}
