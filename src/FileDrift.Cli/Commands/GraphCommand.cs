// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core.Configuration;

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
                CliOutput.Write(new
                {
                    verb = "graph.show",
                    file = GraphSettingsStore.DefaultPath,
                    configured = issues.Count == 0,
                    clientId = s.ClientId,
                    tenantId = s.TenantId,
                    cloud = s.Cloud,
                    allowedRestrictedClouds = s.AllowedRestrictedClouds,
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
}
