// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Covers the auth plumbing that can be verified without a live tenant: scope qualification, cloud
/// resource derivation, AuthMode validation, and the no-prompt contract. Token acquisition itself
/// needs real credentials and is exercised by hand via <c>graph login</c> / <c>graph whoami</c>.
/// </summary>
public class GraphAuthTests
{
    private static GraphSettings Valid() => new()
    {
        ClientId = "11111111-2222-3333-4444-555555555555",
        TenantId = "66666666-7777-8888-9999-aaaaaaaaaaaa",
    };

    [Fact]
    public void Scopes_are_qualified_against_the_graph_resource()
    {
        // MSAL needs absolute scope URIs. Short names are ambiguous across national clouds, so this
        // is the thing that would silently request the wrong resource.
        var s = Valid();
        Assert.Equal(
            ["https://graph.microsoft.com/Files.Read.All", "https://graph.microsoft.com/Sites.Read.All"],
            s.QualifiedScopes);
    }

    [Fact]
    public void Scope_qualification_follows_the_configured_national_cloud()
    {
        var gov = Valid();
        gov.Cloud = GraphCloud.UsGov;
        Assert.All(gov.QualifiedScopes, scope => Assert.StartsWith("https://graph.microsoft.us/", scope));

        var dod = Valid();
        dod.Cloud = GraphCloud.UsGovDoD;
        Assert.All(dod.QualifiedScopes, scope => Assert.StartsWith("https://dod-graph.microsoft.us/", scope));
    }

    [Fact]
    public void Graph_resource_drops_the_api_version_segment()
    {
        // The resource for a token is the host, NOT the versioned API base used for requests.
        var s = Valid();
        Assert.Equal("https://graph.microsoft.com/v1.0", s.EffectiveGraphBaseUrl);
        Assert.Equal("https://graph.microsoft.com", s.GraphResource);
    }

    [Fact]
    public void Graph_resource_handles_an_override_without_a_version_segment()
    {
        // An override may or may not carry a version; stripping must not eat part of the host.
        var s = Valid();
        s.GraphBaseUrlOverride = "https://graph.example.gov";
        Assert.Equal("https://graph.example.gov", s.GraphResource);

        s.GraphBaseUrlOverride = "https://graph.example.gov/beta";
        Assert.Equal("https://graph.example.gov", s.GraphResource);
    }

    [Fact]
    public void An_already_absolute_scope_is_passed_through_unchanged()
    {
        var s = Valid();
        s.Scopes = ["https://graph.microsoft.com/Files.ReadWrite.All", "Sites.Read.All"];
        Assert.Equal(
            ["https://graph.microsoft.com/Files.ReadWrite.All", "https://graph.microsoft.com/Sites.Read.All"],
            s.QualifiedScopes);
    }

    [Theory]
    [InlineData("Interactive")]
    [InlineData("DeviceCode")]
    [InlineData("devicecode")]
    [InlineData("INTERACTIVE")]
    public void Valid_auth_modes_are_accepted_case_insensitively(string mode)
    {
        var s = Valid();
        s.AuthMode = mode;
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void An_unknown_auth_mode_is_rejected()
    {
        var s = Valid();
        s.AuthMode = "Magic";
        Assert.Contains(s.Validate(), i => i.Contains("AuthMode"));
    }

    [Fact]
    public void Default_auth_mode_is_interactive()
    {
        Assert.Equal(GraphAuthModes.Interactive, new GraphSettings().AuthMode);
        Assert.False(GraphAuthModes.IsDeviceCode(new GraphSettings().AuthMode));
    }

    [Fact]
    public async Task Token_acquisition_refuses_to_run_on_an_invalid_configuration()
    {
        // Unconfigured settings must fail with the actionable config message, not an MSAL stack trace.
        using var t = new TempDir();
        var auth = new GraphAuthenticator(new GraphSettings(), t.Path);

        var ex = await Assert.ThrowsAsync<GraphAuthException>(() => auth.GetAccessTokenAsync());
        Assert.Contains("not configured correctly", ex.Message);
        Assert.Contains("ClientId", ex.Message);
    }

    [Fact]
    public async Task Token_acquisition_never_prompts_and_asks_for_graph_login_instead()
    {
        // THE CONTRACT: a run must fail fast rather than open a browser, or an unattended verify would
        // hang forever. An empty cache directory means no account, so this must throw immediately.
        using var t = new TempDir();
        var auth = new GraphAuthenticator(Valid(), t.Path);

        var ex = await Assert.ThrowsAsync<GraphSignInRequiredException>(() => auth.GetAccessTokenAsync());
        Assert.Contains("graph login", ex.Message);
    }

    [Fact]
    public async Task No_signed_in_account_is_reported_as_null_not_an_error()
    {
        using var t = new TempDir();
        Assert.Null(await new GraphAuthenticator(Valid(), t.Path).GetSignedInAccountAsync());
    }

    [Fact]
    public async Task Signing_out_with_nothing_cached_is_a_no_op()
    {
        using var t = new TempDir();
        Assert.Equal(0, await new GraphAuthenticator(Valid(), t.Path).SignOutAsync());
    }

    [Fact]
    public async Task Device_code_mode_without_a_prompt_callback_fails_clearly()
    {
        using var t = new TempDir();
        var s = Valid();
        s.AuthMode = GraphAuthModes.DeviceCode;

        var ex = await Assert.ThrowsAsync<GraphAuthException>(
            () => new GraphAuthenticator(s, t.Path).SignInAsync(deviceCodePrompt: null));
        Assert.Contains("DeviceCode", ex.Message);
    }
}
