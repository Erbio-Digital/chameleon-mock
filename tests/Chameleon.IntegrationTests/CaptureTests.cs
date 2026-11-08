using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Every connector call is captured with sanitized payloads.
public sealed class CaptureTests
{
    [Fact]
    public async Task Inbound_call_is_recorded_with_request_response_status_duration_and_correlation()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        http.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-c03-h-001");

        var resp = await CreateCob(http, txid);
        Assert.Equal("corr-c03-h-001", resp.Headers.GetValues("X-Correlation-Id").Single());

        var call = (await Inbound(http, txid)).Single();
        Assert.Equal("PUT", call.GetProperty("method").GetString());
        Assert.Equal($"/bradesco/v2/cob/{txid}", call.GetProperty("path").GetString());
        Assert.Equal(201, call.GetProperty("statusCode").GetInt32());
        Assert.True(call.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal("corr-c03-h-001", call.GetProperty("correlationId").GetString());
        Assert.Contains("123.45", call.GetProperty("requestBody").GetString());
        Assert.Contains("ATIVA", call.GetProperty("responseBody").GetString());
    }

    [Fact]
    public async Task Credentials_are_redacted_and_personal_data_masked()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        var call = (await Inbound(http, txid)).Single();
        Assert.Equal("[redacted]", call.GetProperty("requestHeaders").GetProperty("Authorization").GetString());

        var request = call.GetProperty("requestBody").GetString()!;
        Assert.DoesNotContain("12345678909", request);
        Assert.DoesNotContain("Joao Silva", request);
        Assert.Contains("12*******09", request);
        Assert.Contains("123.45", request); // non-personal data stays readable

        var response = call.GetProperty("responseBody").GetString()!;
        Assert.DoesNotContain("12345678909", response);

        var token = (await Journal(http, "connector=bradesco"))
            .Single(e => e.GetProperty("path").GetString() == "/bradesco/v2/oauth/token");
        Assert.DoesNotContain(http.DefaultRequestHeaders.Authorization!.Parameter!, token.GetProperty("responseBody").GetString());
    }

    [Fact]
    public void Nested_personal_data_and_body_secret_are_masked()
    {
        var (body, _) = Chameleon.Engine.PayloadSanitizer.Body(
            """{"apiToken":"token-secreto-123","customer":{"document":"12345678000199","email":"ana@example.test"},"items":[{"phone":"11987654321","sku":"abc-1"},{"phone":""}]}""");

        Assert.DoesNotContain("token-secreto-123", body);
        Assert.Contains("[redacted]", body);
        Assert.DoesNotContain("12345678000199", body);
        Assert.DoesNotContain("ana@example.test", body);
        Assert.DoesNotContain("11987654321", body);
        Assert.Contains("11*******21", body);
        Assert.Contains("abc-1", body); // non-personal data stays readable
        Assert.Contains("\"phone\":\"\"", body); // empty stays empty, not "****"
    }

    [Fact]
    public async Task Large_body_is_truncated_and_flagged()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();

        var resp = await http.PutAsJsonAsync($"/bradesco/v2/cob/{txid}", new
        {
            calendario = new { expiracao = 3600 },
            valor = new { original = "10.00" },
            chave = "k",
            solicitacaoPagador = new string('x', 70_000),
            webhookUrl = "https://app-under-test.local/w"
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, resp.StatusCode);

        var call = (await Inbound(http, txid)).Single();
        Assert.True(call.GetProperty("truncated").GetBoolean());
        Assert.Equal(64 * 1024, call.GetProperty("requestBody").GetString()!.Length);
    }

    [Fact]
    public async Task Verify_filters_by_correlation_and_kind()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        var pay = new HttpRequestMessage(HttpMethod.Post, $"/_chameleon/bradesco/cob/{txid}/pay");
        pay.Headers.Add("X-Correlation-Id", "corr-pay");
        await http.SendAsync(pay);

        await factory.SettledAsync();
        var sameRequest = await Verify(http, new { correlationId = "corr-pay" });
        var kinds = sameRequest.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).ToList();
        Assert.Equal(new[] { "ControlAction", "StateTransition", "OutboundWebhook" }, kinds);

        var transitions = await Verify(http, new { entityId = txid, kind = "StateTransition" });
        Assert.Equal(2, transitions.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Webhook_is_recorded_with_payload_status_and_content_length()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);

        await factory.SettledAsync();
        Assert.NotNull(Assert.Single(factory.Webhooks.Received).ContentLength);

        var hook = (await Verify(http, new { entityId = txid, kind = "OutboundWebhook" })).GetProperty("matches")[0];
        Assert.Equal(200, hook.GetProperty("statusCode").GetInt32());
        Assert.Contains(txid, hook.GetProperty("requestBody").GetString());
        Assert.True(hook.GetProperty("durationMs").GetInt64() >= 0);
    }

    [Theory] // calls refused by routing still reach the feed: a tester has to see why the app's call failed
    [InlineData("PUT", "/bradesco/v2/cob/{txid}", "form", 415)]
    [InlineData("GET", "/bradesco/v9/inexistente", null, 404)]
    [InlineData("DELETE", "/bradesco/v2/cob/{txid}", null, 405)]
    public async Task Call_refused_before_the_connector_is_recorded(string method, string path, string? form, int status)
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        path = path.Replace("{txid}", NewTxId());

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (form is not null) request.Content = new FormUrlEncodedContent([new("client_secret", "segredo-do-app")]);
        Assert.Equal(status, (int)(await http.SendAsync(request)).StatusCode);

        var call = (await Journal(http, "connector=bradesco")).Single(e => e.GetProperty("path").GetString() == path);
        Assert.Equal("InboundRequest", call.GetProperty("kind").GetString());
        Assert.Equal(method, call.GetProperty("method").GetString());
        Assert.Equal(status, call.GetProperty("statusCode").GetInt32());
        Assert.DoesNotContain("segredo-do-app", call.GetProperty("requestBody").GetString() ?? "");
    }

    [Fact]
    public async Task Unknown_path_outside_connectors_stays_out_of_the_feed()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);

        Assert.Equal(404, (int)(await http.GetAsync("/not-a-connector/x")).StatusCode);
        Assert.DoesNotContain(await Journal(http, ""), e => e.GetProperty("path").GetString() == "/not-a-connector/x");
    }

    private static Task<List<JsonElement>> Inbound(HttpClient http, string txid) =>
        Journal(http, $"entityId={txid}&kind=InboundRequest");

    private static async Task<List<JsonElement>> Journal(HttpClient http, string query) =>
        (await http.GetFromJsonAsync<JsonElement>($"/_chameleon/journal?{query}")).EnumerateArray().ToList();
}
