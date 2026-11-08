using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco OAuth as the real API works: two token routes, both form-urlencoded.
public sealed class BradescoTokenTests
{
    [Fact] // QRCODEPIX and TRANSFER: POST v2/oauth/token, client in HTTP Basic
    public async Task Pix_token_takes_the_client_in_basic()
    {
        var (http, issued) = await Subscribed();

        var token = await TestAuth.BradescoToken(http, issued["CLIENT_ID"], issued["CLIENT_SECRET"]);

        token.AssertStatus(HttpStatusCode.OK);
        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.True(body.GetProperty("expires_in").GetInt32() > 0);
    }

    [Fact] // ACCOUNT and TED: POST auth/server-mtls/v2/token, client in the form
    public async Task Account_token_takes_the_client_in_the_form()
    {
        var (http, issued) = await Subscribed();

        var token = await http.PostAsync("bradesco/auth/server-mtls/v2/token", new FormUrlEncodedContent([
            new("grant_type", "client_credentials"), new("client_id", issued["CLIENT_ID"]), new("client_secret", issued["CLIENT_SECRET"])]));

        token.AssertStatus(HttpStatusCode.OK);
        var journal = await http.GetStringAsync("_chameleon/journal?connector=bradesco");
        Assert.DoesNotContain(issued["CLIENT_SECRET"], journal);
    }

    [Fact]
    public async Task Json_body_is_refused_and_shows_in_the_feed()
    {
        var (http, issued) = await Subscribed();

        var token = await http.PostAsJsonAsync("bradesco/v2/oauth/token",
            new { grantType = "client_credentials", clientId = issued["CLIENT_ID"], clientSecret = issued["CLIENT_SECRET"] });

        token.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Contains("invalid_request", await token.Content.ReadAsStringAsync());
        Assert.Contains(await Journal(http), e => e.GetProperty("path").GetString() == "/bradesco/v2/oauth/token"
            && e.GetProperty("statusCode").GetInt32() == 400);
    }

    [Fact]
    public async Task Missing_basic_or_wrong_secret_is_refused()
    {
        var (http, issued) = await Subscribed();

        var noClient = await http.PostAsync("bradesco/v2/oauth/token",
            new FormUrlEncodedContent([new("grant_type", "client_credentials")]));
        noClient.AssertStatus(HttpStatusCode.BadRequest);

        var wrongSecret = await TestAuth.BradescoToken(http, issued["CLIENT_ID"], "outro");
        wrongSecret.AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Contains("invalid_client", await wrongSecret.Content.ReadAsStringAsync());
    }

    [Fact] // the old JSON route never existed at the bank: 404, and it reaches the feed
    public async Task Former_json_route_is_not_found()
    {
        var (http, issued) = await Subscribed();

        var old = await http.PostAsJsonAsync("bradesco/oauth/token",
            new { grantType = "client_credentials", clientId = issued["CLIENT_ID"], clientSecret = issued["CLIENT_SECRET"] });

        old.AssertStatus(HttpStatusCode.NotFound);
        Assert.Contains(await Journal(http), e => e.GetProperty("path").GetString() == "/bradesco/oauth/token"
            && e.GetProperty("statusCode").GetInt32() == 404);
    }

    private static async Task<(HttpClient Http, Dictionary<string, string> Issued)> Subscribed()
    {
        var factory = new ChameleonFactory();
        var sandbox = "ci-token-" + Guid.NewGuid().ToString("N")[..8];
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        return (http, await TestAuth.Subscribe(http, "bradesco"));
    }

    private static async Task<List<JsonElement>> Journal(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>("_chameleon/journal?connector=bradesco&kind=InboundRequest")).EnumerateArray().ToList();
}
