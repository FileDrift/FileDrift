// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Configuration;
using Xunit;

namespace FileDrift.Core.Tests;

public class GraphSettingsTests
{
    private static GraphSettings Valid() => new()
    {
        ClientId = "11111111-2222-3333-4444-555555555555",
        TenantId = "66666666-7777-8888-9999-aaaaaaaaaaaa",
    };

    [Fact]
    public void Defaults_are_unconfigured_but_not_broken()
    {
        var s = new GraphSettings();
        Assert.False(s.IsConfigured);
        // Missing IDs are the only complaints — defaults for everything else must be valid, so a
        // fresh config file only needs the two values the user actually has to supply.
        Assert.All(s.Validate(), issue =>
            Assert.True(issue.Contains("ClientId") || issue.Contains("TenantId"), issue));
    }

    [Fact]
    public void Valid_settings_report_configured()
    {
        var s = Valid();
        Assert.Empty(s.Validate());
        Assert.True(s.IsConfigured);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Rejects_a_non_guid_client_id(string clientId)
    {
        var s = Valid();
        s.ClientId = clientId;
        Assert.Contains(s.Validate(), i => i.Contains("ClientId"));
    }

    [Fact]
    public void Accepts_a_tenant_domain_as_well_as_a_guid()
    {
        // Single-tenant registrations are usually referenced by GUID, but contoso.onmicrosoft.com works too.
        var s = Valid();
        s.TenantId = "contoso.onmicrosoft.com";
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Government_clouds_resolve_to_their_own_endpoints()
    {
        // A GCC High tenant is not on the commercial endpoints — getting this wrong would fail auth
        // in a way that looks like a credential problem.
        var gov = Valid();
        gov.Cloud = GraphCloud.UsGov;
        Assert.Equal("https://login.microsoftonline.us", gov.EffectiveAuthorityHost);
        Assert.Equal("https://graph.microsoft.us/v1.0", gov.EffectiveGraphBaseUrl);

        var dod = Valid();
        dod.Cloud = GraphCloud.UsGovDoD;
        Assert.Equal("https://dod-graph.microsoft.us/v1.0", dod.EffectiveGraphBaseUrl);

        var global = Valid();
        Assert.Equal("https://graph.microsoft.com/v1.0", global.EffectiveGraphBaseUrl);
    }

    [Fact]
    public void Default_settings_do_not_reach_any_non_us_endpoint()
    {
        // The out-of-the-box posture: commercial endpoints only, China off.
        var s = new GraphSettings();
        Assert.Equal(GraphCloud.Global, s.Cloud);
        Assert.Empty(s.AllowedRestrictedClouds);
        Assert.Contains("microsoftonline.com", s.EffectiveAuthorityHost);
        Assert.Contains("graph.microsoft.com", s.EffectiveGraphBaseUrl);
    }

    [Fact]
    public void Cloud_name_is_case_insensitive()
    {
        var s = Valid();
        s.Cloud = "usgov";
        Assert.Empty(s.Validate());
        Assert.Equal("https://graph.microsoft.us/v1.0", s.EffectiveGraphBaseUrl);
    }

    [Fact]
    public void Restricted_clouds_are_refused_by_default_with_a_stated_reason()
    {
        // Not typo-tolerance: reaching a foreign-operated cloud should be a deliberate act, not
        // something a one-word config value does by accident. The message must say WHY, so the
        // decision to override is an informed one.
        var s = Valid();
        s.Cloud = GraphCloud.China;

        var issue = Assert.Single(s.Validate());
        Assert.Contains("restricted by default", issue);
        Assert.Contains("21Vianet", issue);                 // the actual reason, not just a refusal
        Assert.Contains("AllowedRestrictedClouds", issue);  // and the exact way to override it
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public void A_restricted_cloud_works_when_explicitly_permitted()
    {
        // The support is real, not a stub — opting in must yield the correct 21Vianet endpoints.
        var s = Valid();
        s.Cloud = GraphCloud.China;
        s.AllowedRestrictedClouds = [GraphCloud.China];

        Assert.Empty(s.Validate());
        Assert.Equal("https://login.chinacloudapi.cn", s.EffectiveAuthorityHost);
        Assert.Equal("https://microsoftgraph.chinacloudapi.cn/v1.0", s.EffectiveGraphBaseUrl);
    }

    [Fact]
    public void Permitting_one_restricted_cloud_does_not_permit_others()
    {
        // Guards the generalized mechanism: the allow-list is per-cloud, not a global "unlock".
        var s = Valid();
        s.AllowedRestrictedClouds = ["SomeOtherRestrictedCloud"];
        s.Cloud = GraphCloud.China;
        Assert.Contains(s.Validate(), i => i.Contains("restricted by default"));
    }

    [Fact]
    public void Unrestricted_clouds_need_no_opt_in()
    {
        foreach (var cloud in new[] { GraphCloud.Global, GraphCloud.UsGov, GraphCloud.UsGovDoD })
        {
            var s = Valid();
            s.Cloud = cloud;
            Assert.Null(GraphCloud.RestrictionFor(cloud));
            Assert.Empty(s.Validate()); // AllowedRestrictedClouds left empty
        }
    }

    [Fact]
    public void The_restriction_check_is_case_insensitive_on_both_sides()
    {
        var s = Valid();
        s.Cloud = "china";
        Assert.Contains(s.Validate(), i => i.Contains("restricted by default"));

        s.AllowedRestrictedClouds = ["CHINA"]; // permitting is case-insensitive too
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Error_messages_do_not_advertise_the_disabled_cloud()
    {
        // Suggesting "use Global, UsGov, UsGovDoD, or China" and then rejecting China would be a
        // confusing loop.
        var s = Valid();
        s.Cloud = "Atlantis";
        var message = Assert.Single(s.Validate());
        Assert.DoesNotContain("China", message);
    }

    [Fact]
    public void Unknown_cloud_is_rejected_unless_endpoints_are_given_explicitly()
    {
        var s = Valid();
        s.Cloud = "Atlantis";
        Assert.Contains(s.Validate(), i => i.Contains("not recognized"));

        // ...but an explicit override is the documented escape hatch, so it must clear the error.
        s.AuthorityHostOverride = "https://login.example.gov";
        s.GraphBaseUrlOverride = "https://graph.example.gov/v1.0";
        Assert.Empty(s.Validate());
        Assert.Equal("https://graph.example.gov/v1.0", s.EffectiveGraphBaseUrl);
    }

    [Fact]
    public void Authority_combines_host_and_tenant()
    {
        var s = Valid();
        Assert.Equal($"https://login.microsoftonline.com/{s.TenantId}", s.Authority);
        Assert.Null(new GraphSettings().Authority); // no tenant yet
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Rejects_out_of_range_page_size(int size)
    {
        var s = Valid();
        s.PageSize = size;
        Assert.Contains(s.Validate(), i => i.Contains("PageSize"));
    }

    [Fact]
    public void Round_trips_through_the_json_file()
    {
        using var t = new TempDir();
        var path = t.Sub("graph.json");

        var original = Valid();
        original.Cloud = GraphCloud.UsGov;
        original.Scopes = ["Files.Read.All"];
        original.PageSize = 50;
        GraphSettingsStore.Save(original, path);

        var loaded = GraphSettingsStore.LoadFileOnly(path);
        Assert.Equal(original.ClientId, loaded.ClientId);
        Assert.Equal(original.TenantId, loaded.TenantId);
        Assert.Equal(GraphCloud.UsGov, loaded.Cloud);
        Assert.Equal(["Files.Read.All"], loaded.Scopes);
        Assert.Equal(50, loaded.PageSize);
    }

    [Fact]
    public void Derived_values_are_not_written_to_the_file()
    {
        // graph.json is meant to be hand-edited. Persisting computed values (Authority, IsConfigured,
        // the Effective* endpoints) invites someone to edit one and wonder why it has no effect, and
        // they'd go stale the moment Cloud changes.
        using var t = new TempDir();
        var path = t.Sub("graph.json");
        GraphSettingsStore.Save(Valid(), path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("Authority", json);
        Assert.DoesNotContain("EffectiveGraphBaseUrl", json);
        Assert.DoesNotContain("IsConfigured", json);
        // ...while the settings the user actually controls are present.
        Assert.Contains("ClientId", json);
        Assert.Contains("Cloud", json);
    }

    [Fact]
    public void Missing_or_malformed_file_loads_as_unconfigured_rather_than_throwing()
    {
        using var t = new TempDir();

        Assert.False(GraphSettingsStore.LoadFileOnly(t.Sub("does-not-exist.json")).IsConfigured);

        var bad = t.Sub("bad.json");
        File.WriteAllText(bad, "{ this is not json");
        Assert.False(GraphSettingsStore.LoadFileOnly(bad).IsConfigured);
    }

    [Fact]
    public void LoadFileOnly_ignores_environment_overrides_so_a_save_cannot_bake_them_in()
    {
        // The read-modify-write path (`graph set`) must not persist a transient env override into the
        // user's file — that would silently pin a CI value into their real config.
        using var t = new TempDir();
        var path = t.Sub("graph.json");
        GraphSettingsStore.Save(Valid(), path);

        var previous = Environment.GetEnvironmentVariable(GraphSettingsStore.ClientIdEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GraphSettingsStore.ClientIdEnvVar, "11111111-1111-1111-1111-111111111111");

            Assert.Equal("11111111-1111-1111-1111-111111111111", GraphSettingsStore.Load(path).ClientId);
            Assert.Equal(Valid().ClientId, GraphSettingsStore.LoadFileOnly(path).ClientId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GraphSettingsStore.ClientIdEnvVar, previous);
        }
    }

    [Fact]
    public void EnsureTemplate_creates_once_and_does_not_clobber()
    {
        using var t = new TempDir();
        var path = t.Sub("graph.json");

        var (_, created) = GraphSettingsStore.EnsureTemplate(path);
        Assert.True(created);
        Assert.True(File.Exists(path));

        // Write real values, then confirm a second call leaves them alone.
        GraphSettingsStore.Save(Valid(), path);
        var (_, createdAgain) = GraphSettingsStore.EnsureTemplate(path);
        Assert.False(createdAgain);
        Assert.Equal(Valid().ClientId, GraphSettingsStore.LoadFileOnly(path).ClientId);
    }
}
