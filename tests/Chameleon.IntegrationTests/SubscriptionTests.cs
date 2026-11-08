using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Connector subscription per sandbox.
public sealed class SubscriptionTests
{
    [Fact]
    public async Task Subscribed_connector_issues_credentials_that_the_service_uses()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        Assert.Equal("Available", await Status(qa, "bradesco"));

        Assert.Equal(HttpStatusCode.Created, (await qa.PostAsync("_chameleon/connectors/bradesco/subscription", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await qa.PostAsync("_chameleon/connectors/bradesco/subscription", null)).StatusCode);
        var issued = await TestAuth.Subscribe(qa, "bradesco");
        Assert.EndsWith("/s/qa/bradesco", issued["BASE_URL"]);
        Assert.Equal(issued["BASE_URL"] + "/v2/oauth/token", issued["TOKEN_URL"]);
        Assert.NotEmpty(issued["CLIENT_SECRET"]);

        var token = await TestAuth.BradescoToken(qa, issued["CLIENT_ID"], issued["CLIENT_SECRET"]);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        qa.DefaultRequestHeaders.Authorization = new("Bearer", (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(qa, NewTxId())).StatusCode);
        Assert.Equal("Active", await Status(qa, "bradesco"));

        // The changes are journaled without secrets, the secret is stored sealed.
        var journal = await qa.GetStringAsync("_chameleon/journal?connector=bradesco");
        Assert.DoesNotContain(issued["CLIENT_SECRET"], journal);
    }

    [Fact]
    public async Task Unsubscribed_connector_is_refused_outside_default()
    {
        using var factory = new ChameleonFactory();
        await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-1" });
        var ci = Client(factory, "ci-1");

        var refused = await TestAuth.BradescoToken(ci, "x");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("connector not subscribed", await refused.Content.ReadAsStringAsync());
        var journal = await ci.GetFromJsonAsync<JsonElement>("_chameleon/journal?connector=bradesco");
        Assert.Equal(404, journal.EnumerateArray().Single().GetProperty("statusCode").GetInt32());

        var local = await TestAuth.BradescoToken(factory.CreateClient(), "qualquer");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Wrong_other_sandbox_rotated_or_cancelled_credentials_do_not_issue_tokens()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var qa = Client(factory, "qa");
        var hml = Client(factory, "hml");
        var fromQa = await TestAuth.Subscribe(qa, "bradesco");
        await TestAuth.Subscribe(hml, "bradesco");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(hml, fromQa)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(qa, new Credentials(fromQa["CLIENT_ID"], "errado"))).StatusCode);

        var before = await Token(qa, fromQa);
        qa.DefaultRequestHeaders.Authorization = new("Bearer", (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await qa.PostAsync("_chameleon/connectors/bradesco/subscription/rotate", null)).StatusCode);
        // Rotation also replaced the certificate: the old one is refused before any credential check.
        Assert.Equal(HttpStatusCode.Forbidden, (await Token(qa, fromQa)).StatusCode);
        var rotated = await TestAuth.Subscribe(qa, "bradesco"); // presents the new certificate
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(qa, fromQa)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(qa, NewTxId())).StatusCode); // token issued before: still valid

        Assert.Equal(HttpStatusCode.OK, (await Token(qa, rotated)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await qa.DeleteAsync("_chameleon/connectors/bradesco/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Token(qa, rotated)).StatusCode);
        Assert.Equal("Available", await Status(qa, "bradesco"));
    }

    [Fact]
    public async Task Nfse_stays_pending_without_callback_and_delivers_once_filled()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        var issued = await TestAuth.Subscribe(qa, "nfse-sp");
        Assert.Equal("Pending", await Status(qa, "nfse-sp"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await qa.PostAsJsonAsync("nfse-sp/nfse", new { valor = 10m })).StatusCode);
        qa.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{issued["USERNAME"]}:{issued["PASSWORD"]}")));

        await EmitAndAuthorize(qa, "rps-1");
        var notSent = await Verify(qa, new { connector = "nfse-sp", entityId = "rps-1", kind = "OutboundWebhook" });
        Assert.Equal("pending configuration: missing Callback URL", notSent.GetProperty("matches")[0].GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await qa.PutAsJsonAsync("_chameleon/connectors/nfse-sp/subscription/settings", new { callbackUrl = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await qa.PutAsJsonAsync("_chameleon/connectors/nfse-sp/subscription/settings", new { outro = "x" })).StatusCode);
        const string callback = "https://app-under-test.local/webhooks/nfse";
        (await qa.PutAsJsonAsync("_chameleon/connectors/nfse-sp/subscription/settings", new { callbackUrl = callback })).EnsureSuccessStatusCode();
        Assert.Equal("Active", await Status(qa, "nfse-sp"));

        await EmitAndAuthorize(qa, "rps-2");
        await factory.SettledAsync("qa");
        Assert.Equal(callback, Assert.Single(factory.Webhooks.Received).Url);
    }

    [Fact]
    public async Task Test_event_goes_to_configured_destinations_and_needs_one()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Subscribe(qa, "bradesco");

        var none = await qa.PostAsync("_chameleon/connectors/bradesco/subscription/test-event", null);
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);

        await TestAuth.Bradesco(qa, "qa"); // registers the test key's webhook
        Assert.Equal(HttpStatusCode.Accepted, (await qa.PostAsync("_chameleon/connectors/bradesco/subscription/test-event", null)).StatusCode);
        await factory.SettledAsync("qa");
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(WebhookUrl, url);
        Assert.Equal("""{"pix":[]}""", body);

        var delivery = (await qa.GetFromJsonAsync<JsonElement>("_chameleon/webhooks"))[0];
        Assert.Equal("test-event", delivery.GetProperty("entityId").GetString());
        Assert.Equal("test event", delivery.GetProperty("note").GetString());
        Assert.Empty((await qa.GetFromJsonAsync<JsonElement>("_chameleon/connectors/bradesco/entities/cob")).EnumerateArray());
    }

    [Fact]
    public async Task Copy_keeps_subscriptions_and_credentials()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        var issued = await TestAuth.Subscribe(qa, "bradesco");
        await TestAuth.Subscribe(qa, "nfse-sp");
        await qa.PutAsJsonAsync("_chameleon/connectors/nfse-sp/subscription/settings", new { callbackUrl = "https://app-under-test.local/nfse" });

        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-2", from = "qa" })).EnsureSuccessStatusCode();
        var copy = Client(factory, "ci-2");
        // The copy takes the same certificate as the source.
        var qaCert = (await qa.GetFromJsonAsync<JsonElement>("_chameleon/connectors/bradesco/subscription")).GetProperty("certificate").GetProperty("pem").GetString()!;
        TestAuth.Present(copy, qaCert);
        Assert.Equal("Active", await Status(copy, "bradesco"));
        Assert.Equal("Active", await Status(copy, "nfse-sp"));
        Assert.Equal(HttpStatusCode.OK, (await Token(copy, issued)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await copy.DeleteAsync("_chameleon/connectors/bradesco/subscription")).StatusCode);
        Assert.Equal("Active", await Status(qa, "bradesco"));
    }

    [Fact]
    public void Secret_box_round_trips_and_refuses_a_bad_key()
    {
        var box = new Chameleon.Engine.SecretBox(ChameleonFactory.SecretKey);
        var sealedValue = box.Encrypt("segredo");
        Assert.NotEqual(sealedValue, box.Encrypt("segredo")); // fresh nonce each time
        Assert.Equal("segredo", box.Decrypt(sealedValue));
        Assert.Throws<InvalidOperationException>(() => new Chameleon.Engine.SecretBox(null));
        Assert.Throws<InvalidOperationException>(() => new Chameleon.Engine.SecretBox(Convert.ToBase64String(new byte[16])));
    }

    private sealed record Credentials(string Id, string Secret);

    private static Task<HttpResponseMessage> Token(HttpClient http, Dictionary<string, string> issued) =>
        Token(http, new Credentials(issued["CLIENT_ID"], issued["CLIENT_SECRET"]));

    private static Task<HttpResponseMessage> Token(HttpClient http, Credentials c) =>
        TestAuth.BradescoToken(http, c.Id, c.Secret);

    private static async Task EmitAndAuthorize(HttpClient http, string rps)
    {
        (await http.PostAsJsonAsync("nfse-sp/nfse", new { valor = 10m, rps })).EnsureSuccessStatusCode();
        (await http.PostAsync($"_chameleon/connectors/nfse-sp/entities/nfse/{rps}/actions/authorize", null)).EnsureSuccessStatusCode();
    }

    private static async Task<string?> Status(HttpClient http, string connector) =>
        (await http.GetFromJsonAsync<JsonElement>("_chameleon/subscriptions")).EnumerateArray()
            .Single(s => s.GetProperty("connector").GetString() == connector).GetProperty("status").GetString();

    private static HttpClient Client(ChameleonFactory factory, string sandbox)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        return http;
    }
}
