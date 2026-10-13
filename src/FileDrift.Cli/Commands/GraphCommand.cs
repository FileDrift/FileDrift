// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using FileDrift.Core.Persistence;

namespace FileDrift.Cli.Commands;

internal static class GraphCommand
{
    internal static Command Build()
    {
        var cmd = new Command("graph",
            "Configure SharePoint Online / OneDrive access (Microsoft Graph). Settings are stored in " +
            "graph.json under %APPDATA%\\FileDrift and shared with the app.");
        cmd.Add(BuildShow());
        cmd.Add(BuildSet());
        cmd.Add(BuildLogin());
        cmd.Add(BuildLogout());
        cmd.Add(BuildWhoami());
        return cmd;
    }

    private static Command BuildShow()
    {
        var cmd = new Command("show", "Show the current Graph configuration and whether it is usable.");
        cmd.SetAction((_, _) =>
        {
            try
            {
                var s = GraphSettingsStore.Load();
                var issues = s.Validate();
                // Best-effort: an unconfigured or unreadable cache must not break `show`, which is
                // exactly what you run to diagnose that state.
                GraphAccount? account = null;
                if (issues.Count == 0)
                {
                    try { account = new GraphAuthenticator(s).GetSignedInAccountAsync().GetAwaiter().GetResult(); }
                    catch { /* report as not signed in */ }
                }
                CliOutput.Write(new
                {
                    verb = "graph.show",
                    file = GraphSettingsStore.DefaultPath,
                    configured = issues.Count == 0,
                    clientId = s.ClientId,
                    tenantId = s.TenantId,
                    cloud = s.Cloud,
                    allowedRestrictedClouds = s.AllowedRestrictedClouds,
                    authMode = s.AuthMode,
                    signedInAs = account?.Username,
                    tokenCache = AppPaths.GraphTokenCache,
                    authority = s.Authority,
                    graphBaseUrl = s.EffectiveGraphBaseUrl,
                    scopes = s.Scopes,
                    redirectUri = s.RedirectUri,
                    pageSize = s.PageSize,
                    maxRetries = s.MaxRetries,
                    issues,
                });
                return Task.FromResult(0);
            }
            catch (Exception ex) { return Task.FromResult(CliOutput.Error("graph.show", ex.Message, ex.GetType().Name)); }
        });
        return cmd;
    }

    private static Command BuildSet()
    {
        var clientId = new Option<string>("--client-id") { Description = "Application (client) ID from the Azure AD app registration." };
        var tenantId = new Option<string>("--tenant-id") { Description = "Directory (tenant) ID (GUID or domain)." };
        var cloud = new Option<string>("--cloud") { Description = $"Microsoft cloud: {GraphCloud.UnrestrictedList}. Default Global (commercial)." };
        var scopes = new Option<string>("--scopes") { Description = "Comma-separated delegated Graph scopes. Default: Files.Read.All,Sites.Read.All" };
        var pageSize = new Option<int?>("--page-size") { Description = "Items per Graph page while enumerating (1–999)." };
        var maxRetries = new Option<int?>("--max-retries") { Description = "Retries for throttled (429) or transient Graph responses." };

        var cmd = new Command("set",
            "Set Graph configuration values. Only the options you pass are changed; the rest are left as-is. " +
            "No secret is involved — FileDrift uses delegated interactive sign-in, not a client secret.");
        foreach (var o in new Option[] { clientId, tenantId, cloud, scopes, pageSize, maxRetries })
            cmd.Add(o);

        cmd.SetAction((parseResult, _) =>
        {
            try
            {
                // Read the FILE (not Load(), which layers env vars on top) so an environment override
                // present in this shell isn't accidentally written into the user's config.
                var s = GraphSettingsStore.LoadFileOnly();

                if (parseResult.GetValue(clientId) is { Length: > 0 } c) s.ClientId = c.Trim();
                if (parseResult.GetValue(tenantId) is { Length: > 0 } t) s.TenantId = t.Trim();
                if (parseResult.GetValue(cloud) is { Length: > 0 } cl) s.Cloud = cl.Trim();
                if (parseResult.GetValue(scopes) is { Length: > 0 } sc)
                    s.Scopes = sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parseResult.GetValue(pageSize) is { } ps) s.PageSize = ps;
                if (parseResult.GetValue(maxRetries) is { } mr) s.MaxRetries = mr;

                GraphSettingsStore.Save(s);

                var issues = s.Validate();
                CliOutput.Write(new
                {
                    verb = "graph.set",
                    file = GraphSettingsStore.DefaultPath,
                    configured = issues.Count == 0,
                    clientId = s.ClientId,
                    tenantId = s.TenantId,
                    cloud = s.Cloud,
                    issues,
                });
                return Task.FromResult(issues.Count == 0 ? 0 : 1);
            }
            catch (Exception ex) { return Task.FromResult(CliOutput.Error("graph.set", ex.Message, ex.GetType().Name)); }
        });
        return cmd;
    }

    private static Command BuildLogin()
    {
        var cmd = new Command("login",
            "Sign in to Microsoft Graph interactively. The token is cached, so later runs do not prompt. " +
            "This is the only command that opens a sign-in prompt -- verify runs never do, so an " +
            "unattended run fails with a clear message instead of hanging on a browser.");
        cmd.SetAction(async (_, ct) =>
        {
            try
            {
                var settings = GraphSettingsStore.Load();
                var auth = new GraphAuthenticator(settings);

                // Device-code instructions must reach the user on stderr: stdout may be redirected to
                // a file or piped to jq, and a code nobody sees is a hang.
                var account = await auth.SignInAsync(
                    deviceCodePrompt: msg => Console.Error.WriteLine(msg),
                    cancellationToken: ct);

                CliOutput.Write(new
                {
                    verb = "graph.login",
                    signedInAs = account.Username,
                    tenantId = account.TenantId,
                    environment = account.Environment,
                    scopes = settings.QualifiedScopes,
                    tokenCache = AppPaths.GraphTokenCache,
                });
                return 0;
            }
            catch (Exception ex) { return CliOutput.Error("graph.login", ex.Message, ex.GetType().Name); }
        });
        return cmd;
    }

    private static Command BuildLogout()
    {
        var cmd = new Command("logout",
            "Remove the cached Microsoft Graph sign-in from this machine. The next run will need " +
            "\"graph login\" again.");
        cmd.SetAction(async (_, ct) =>
        {
            try
            {
                var removed = await new GraphAuthenticator(GraphSettingsStore.Load()).SignOutAsync(ct);
                CliOutput.Write(new
                {
                    verb = "graph.logout",
                    accountsRemoved = removed,
                    tokenCache = AppPaths.GraphTokenCache,
                });
                return 0;
            }
            catch (Exception ex) { return CliOutput.Error("graph.logout", ex.Message, ex.GetType().Name); }
        });
        return cmd;
    }

    private static Command BuildWhoami()
    {
        var cmd = new Command("whoami",
            "Show which account is signed in to Microsoft Graph, and confirm a token can still be " +
            "acquired silently (i.e. that an unattended run would work right now).");
        cmd.SetAction(async (_, ct) =>
        {
            try
            {
                var settings = GraphSettingsStore.Load();
                var auth = new GraphAuthenticator(settings);
                var account = await auth.GetSignedInAccountAsync(ct);
                if (account is null)
                {
                    CliOutput.Write(new
                    {
                        verb = "graph.whoami",
                        signedIn = false,
                        message = "Not signed in. Run \"FileDrift-CLI graph login\".",
                    });
                    return 1;
                }

                // Actually acquire a token rather than just reporting a cached account: a stale refresh
                // token looks fine in the cache but fails at run time, which is the thing worth knowing.
                var tokenOk = true;
                string? tokenError = null;
                try { await auth.GetAccessTokenAsync(ct); }
                catch (Exception ex) { tokenOk = false; tokenError = ex.Message; }

                CliOutput.Write(new
                {
                    verb = "graph.whoami",
                    signedIn = true,
                    signedInAs = account.Username,
                    tenantId = account.TenantId,
                    environment = account.Environment,
                    silentTokenOk = tokenOk,
                    tokenError,
                    scopes = settings.QualifiedScopes,
                });
                return tokenOk ? 0 : 1;
            }
            catch (Exception ex) { return CliOutput.Error("graph.whoami", ex.Message, ex.GetType().Name); }
        });
        return cmd;
    }
}
