using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Chameleon.IntegrationTests.Auth;

/// The shared instance (CHAMELEON_AUTH=oidc): login, roles and the closed "default".
public sealed class OidcInstanceTests
{
    private readonly FakeOidcIssuer _issuer = new();
    private const string CiKey = "chm_ci_test-key-of-the-ci";

    private WebApplicationFactory<Program> Oidc(string? audience = null) =>
        new ChameleonFactory(s => s.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = _issuer))
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("CHAMELEON_AUTH", "oidc")
                    .UseSetting("CHAMELEON_OIDC_ISSUER", FakeOidcIssuer.Issuer)
                    .UseSetting("CHAMELEON_OIDC_CLIENT_ID", FakeOidcIssuer.ClientId)
                    .UseSetting("CHAMELEON_WEBHOOK_ALLOWED_HOSTS", "localhost")
                    .UseSetting("CHAMELEON_CI_KEY", CiKey)
                    .UseSetting("CHAMELEON_ALLOWED_EMAIL_DOMAINS", "example.com, example.org")
                    .UseSetting("CHAMELEON_ALLOWED_EMAILS", "bia@example.net");
                if (audience is not null) b.UseSetting("CHAMELEON_OIDC_AUDIENCE", audience);
            }).WithPersistent("qa");

    private static HttpClient Client(WebApplicationFactory<Program> factory, string bearer, string? basePath = null)
    {
        var http = factory.CreateClient();
        if (basePath is not null) http.BaseAddress = new Uri(http.BaseAddress!, basePath);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    [Fact]
    public async Task Control_api_requires_login_except_public_routes()
    {
        using var factory = Oidc();
        var http = factory.CreateClient();
        var config = await http.GetFromJsonAsync<JsonElement>("/_chameleon/config");
        Assert.Equal("oidc", config.GetProperty("auth").GetString());
        Assert.Equal(FakeOidcIssuer.Issuer, config.GetProperty("oidc").GetProperty("issuer").GetString());
        Assert.Equal(FakeOidcIssuer.ClientId, config.GetProperty("oidc").GetProperty("clientId").GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("oidc").GetProperty("audience").ValueKind);
        Assert.Equal(new[] { "example.com", "example.org" }, config.GetProperty("allowedEmailDomains").EnumerateArray().Select(d => d.GetString()).ToArray());
        Assert.DoesNotContain("bia@example.net", config.ToString());
        (await http.GetAsync("/_chameleon/ca.crt")).AssertStatus(HttpStatusCode.OK);
        (await http.GetAsync("/_chameleon/sandboxes")).AssertStatus(HttpStatusCode.Unauthorized);
        (await http.PostAsync("/_chameleon/mcp", null)).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_token_from_the_discovered_issuer_is_an_operator()
    {
        using var factory = Oidc();
        var me = await Client(factory, _issuer.Token()).GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("OPERATOR", me.GetProperty("role").GetString());
        Assert.Equal("ana@example.com", me.GetProperty("email").GetString());
        Assert.True(_issuer.DiscoveryRequests > 0);
    }

    [Theory] // 401: the token itself is not valid
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("key")]
    public async Task Invalid_token_is_rejected(string broken)
    {
        using var factory = Oidc();
        var token = broken switch
        {
            "issuer" => _issuer.Token(issuer: "https://other.example.com"),
            "audience" => _issuer.Token(audience: "other-client"),
            "expired" => _issuer.Token(expired: true),
            _ => _issuer.Token(signWith: new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test" }),
        };
        (await Client(factory, token).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Theory] // 403: valid token, person not allowed
    [InlineData("ana@example.net", true)]
    [InlineData("ana@sub.example.com", true)]
    [InlineData("ana@example.com", false)]
    [InlineData(null, true)]
    public async Task Allowlist_and_verified_email_are_required(string? email, bool verified)
    {
        using var factory = Oidc();
        (await Client(factory, _issuer.Token(email, verified)).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Connector_logo_is_public_only_for_get_on_the_exact_route()
    {
        using var factory = Oidc();
        var http = factory.CreateClient();
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/_chameleon/connectors/bradesco/logo")).StatusCode);
        (await http.PutAsync("/s/qa/_chameleon/connectors/bradesco/webhook-registrations/logo", null)).AssertStatus(HttpStatusCode.Unauthorized);
        (await http.DeleteAsync("/s/qa/_chameleon/connectors/bradesco/webhook-registrations/logo")).AssertStatus(HttpStatusCode.Unauthorized);
        (await http.GetAsync("/s/qa/_chameleon/connectors/bradesco/entities/x/logo")).AssertStatus(HttpStatusCode.Unauthorized);
        (await http.PostAsync("/s/qa/_chameleon/connectors/bradesco/actions/logo", null)).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exact_email_and_string_verified_claim_are_accepted()
    {
        using var factory = Oidc();
        (await Client(factory, _issuer.Token("bia@example.net", verifiedClaim: "true")).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.OK);
    }

    [Fact] // D2: with an API audience, the access token carries it
    public async Task Configured_audience_replaces_the_client_id()
    {
        using var factory = Oidc(audience: "https://chameleon.example.com");
        (await Client(factory, _issuer.Token(audience: "https://chameleon.example.com")).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.OK);
        (await Client(factory, _issuer.Token()).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact] // D3: a token issued to another client (an MCP client) acts as the operator's agent
    public async Task Token_of_another_client_acts_as_agent()
    {
        using var factory = Oidc(audience: "https://chameleon.example.com");
        var agent = Client(factory, _issuer.Token(audience: "https://chameleon.example.com", azp: "mcp-client"));
        var me = await agent.GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("AGENT", me.GetProperty("role").GetString());
        (await agent.GetAsync("/_chameleon/tokens")).AssertStatus(HttpStatusCode.Forbidden);
        var panel = Client(factory, _issuer.Token(audience: "https://chameleon.example.com", azp: FakeOidcIssuer.ClientId));
        Assert.Equal("OPERATOR", (await panel.GetFromJsonAsync<JsonElement>("/_chameleon/me")).GetProperty("role").GetString());
    }

    [Fact] // RFC 9728: the 401 points the MCP client to the configured issuer
    public async Task Mcp_challenge_leads_to_the_issuer()
    {
        using var factory = Oidc();
        var http = factory.CreateClient();
        var challenge = await http.PostAsync("/s/qa/_chameleon/mcp", null);
        challenge.AssertStatus(HttpStatusCode.Unauthorized);
        var metadataUrl = challenge.Headers.WwwAuthenticate.Single().Parameter!.Split('"')[1];
        var metadata = await http.GetFromJsonAsync<JsonElement>(new Uri(metadataUrl).PathAndQuery);
        Assert.Equal(FakeOidcIssuer.Issuer, metadata.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact] // D8: the live channel takes the token from the query string, and only there
    public async Task Hub_accepts_the_token_in_the_query_string()
    {
        using var factory = Oidc();
        var http = factory.CreateClient();
        var token = _issuer.Token();
        (await http.PostAsync($"/_chameleon/hub/negotiate?negotiateVersion=1&access_token={token}", null)).AssertStatus(HttpStatusCode.OK);
        (await http.PostAsync("/_chameleon/hub/negotiate?negotiateVersion=1", null)).AssertStatus(HttpStatusCode.Unauthorized);
        (await http.GetAsync($"/_chameleon/me?access_token={token}")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task None_mode_has_no_login_and_no_resource_metadata()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        var config = await http.GetFromJsonAsync<JsonElement>("/_chameleon/config");
        Assert.Equal("none", config.GetProperty("auth").GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("oidc").ValueKind);
        Assert.Equal("OPERATOR", (await http.GetFromJsonAsync<JsonElement>("/_chameleon/me")).GetProperty("role").GetString());
        (await http.GetAsync("/.well-known/oauth-protected-resource/_chameleon/mcp")).AssertStatus(HttpStatusCode.NotFound);
    }

    private const string MsRole = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    [Theory] // the provider's own role claim never decides access
    [InlineData("ana@example.net", true)]
    [InlineData("ana@example.com", false)]
    public async Task Provider_role_claim_does_not_grant_access(string email, bool verified)
    {
        using var factory = Oidc();
        var token = _issuer.Token(email, verified, extra: new Dictionary<string, object> { [MsRole] = "OPERATOR" });
        (await Client(factory, token).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact] // a chameleon_role inside the provider token is ignored
    public async Task Incoming_chameleon_role_claim_is_overwritten()
    {
        using var factory = Oidc(audience: "https://chameleon.example.com");
        var token = _issuer.Token(audience: "https://chameleon.example.com", azp: "mcp-client",
            extra: new Dictionary<string, object> { ["chameleon_role"] = "OPERATOR" });
        var me = await Client(factory, token).GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("AGENT", me.GetProperty("role").GetString());
        var forged = _issuer.Token(email: "ana@example.net", audience: "https://chameleon.example.com", extra: new Dictionary<string, object> { ["chameleon_role"] = "OPERATOR" });
        (await Client(factory, forged).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "false")]
    public async Task Missing_or_false_verified_claim_is_forbidden(bool omit, string? claim)
    {
        using var factory = Oidc();
        var token = _issuer.Token(omitVerified: omit, verifiedClaim: claim);
        (await Client(factory, token).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Client_id_claim_without_azp_acts_as_agent()
    {
        using var factory = Oidc();
        var me = await Client(factory, _issuer.Token(clientId: "mcp-client")).GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("AGENT", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Hub_accepts_a_personal_token_in_the_query_string()
    {
        using var factory = Oidc();
        var created = await (await Client(factory, _issuer.Token()).PostAsJsonAsync("/_chameleon/tokens", new { name = "hub" })).Content.ReadFromJsonAsync<JsonElement>();
        var pat = created.GetProperty("token").GetString();
        (await factory.CreateClient().PostAsync($"/_chameleon/hub/negotiate?negotiateVersion=1&access_token={pat}", null)).AssertStatus(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ci_key_is_rejected_when_not_configured()
    {
        using var factory = new ChameleonFactory(s => s.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = _issuer))
            .WithWebHostBuilder(b => b.UseSetting("CHAMELEON_AUTH", "oidc")
                .UseSetting("CHAMELEON_OIDC_ISSUER", FakeOidcIssuer.Issuer)
                .UseSetting("CHAMELEON_OIDC_CLIENT_ID", FakeOidcIssuer.ClientId)
                .UseSetting("CHAMELEON_WEBHOOK_ALLOWED_HOSTS", "localhost")
                .UseSetting("CHAMELEON_ALLOWED_EMAIL_DOMAINS", "example.com"));
        (await Client(factory, CiKey).GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact] // CI uses the API but never resets a permanent sandbox
    public async Task Ci_key_acts_as_ci()
    {
        using var factory = Oidc();
        var ci = Client(factory, CiKey);

        var me = await ci.GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("CI", me.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("email").ValueKind);
        (await ci.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-shared" })).AssertStatus(HttpStatusCode.Created);
        (await ci.PostAsync("/_chameleon/sandboxes/qa/reset?confirm=qa", null)).AssertStatus(HttpStatusCode.Forbidden);
        (await ci.GetAsync("/_chameleon/tokens")).AssertStatus(HttpStatusCode.Forbidden);

        (await Client(factory, CiKey + "x").GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
        (await Client(factory, "chm_pat_does-not-exist").GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact] // The personal token is the agent, revocable by its owner only
    public async Task Personal_token_acts_as_agent_until_revoked()
    {
        using var factory = Oidc();
        var ana = Client(factory, _issuer.Token("ana@example.com"));
        (await ana.PostAsJsonAsync("/_chameleon/tokens", new { name = "" })).AssertStatus(HttpStatusCode.BadRequest);
        (await ana.PostAsJsonAsync("/_chameleon/tokens", new { name = new string('x', 61) })).AssertStatus(HttpStatusCode.BadRequest);

        var created = await (await ana.PostAsJsonAsync("/_chameleon/tokens", new { name = "notebook" })).Content.ReadFromJsonAsync<JsonElement>();
        var value = created.GetProperty("token").GetString()!;
        Assert.StartsWith("chm_pat_", value);
        var agent = Client(factory, value);

        var me = await agent.GetFromJsonAsync<JsonElement>("/_chameleon/me");
        Assert.Equal("AGENT", me.GetProperty("role").GetString());
        Assert.Equal("ana@example.com", me.GetProperty("email").GetString());
        (await agent.GetAsync("/_chameleon/sandboxes")).AssertStatus(HttpStatusCode.OK);
        (await agent.GetAsync("/_chameleon/tokens")).AssertStatus(HttpStatusCode.Forbidden);
        (await agent.PostAsync("/_chameleon/sandboxes/qa/reset?confirm=qa", null)).AssertStatus(HttpStatusCode.Forbidden);

        var listed = Assert.Single((await ana.GetFromJsonAsync<JsonElement>("/_chameleon/tokens")).EnumerateArray().ToList());
        Assert.Equal("notebook", listed.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("lastUsedAt").ValueKind);
        Assert.False(listed.TryGetProperty("token", out _));
        Assert.DoesNotContain(value, listed.ToString());

        var id = created.GetProperty("id").GetString();
        (await Client(factory, _issuer.Token("bia@example.com")).DeleteAsync($"/_chameleon/tokens/{id}")).AssertStatus(HttpStatusCode.NotFound);
        (await ana.DeleteAsync($"/_chameleon/tokens/{id}")).AssertStatus(HttpStatusCode.NoContent);
        (await agent.GetAsync("/_chameleon/me")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact] // The agent gets the certificate, never its private key nor the .pfx
    public async Task Agent_does_not_get_private_key_or_pfx()
    {
        using var factory = Oidc();
        var ana = Client(factory, _issuer.Token("ana@example.com"), "/s/qa/");
        await TestAuth.Subscribe(ana, "bradesco");
        var token = (await (await ana.PostAsJsonAsync("/_chameleon/tokens", new { name = "mcp" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var agent = Client(factory, token, "/s/qa/");

        var asOperator = (await ana.GetFromJsonAsync<JsonElement>("_chameleon/connectors/bradesco/subscription")).GetProperty("certificate");
        var asAgent = (await agent.GetFromJsonAsync<JsonElement>("_chameleon/connectors/bradesco/subscription")).GetProperty("certificate");
        Assert.Contains("PRIVATE KEY", asOperator.GetProperty("keyPem").GetString());
        Assert.Equal(JsonValueKind.Null, asAgent.GetProperty("keyPem").ValueKind);
        Assert.Equal(asOperator.GetProperty("pem").GetString(), asAgent.GetProperty("pem").GetString());

        (await agent.PutAsJsonAsync("_chameleon/connectors/bradesco/subscription/certificate/pfx-password", new { password = "strong-password-1" }))
            .AssertStatus(HttpStatusCode.Forbidden);
        (await agent.GetAsync("_chameleon/connectors/bradesco/subscription/certificate.pfx")).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Default_sandbox_is_closed_to_the_provider_routes()
    {
        using var factory = Oidc();
        var http = factory.CreateClient();
        var token = await TestAuth.BradescoToken(http, "x", "y");
        token.AssertStatus(HttpStatusCode.NotFound);
    }
}
