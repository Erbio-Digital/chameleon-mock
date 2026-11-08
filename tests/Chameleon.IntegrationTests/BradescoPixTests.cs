using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco Pix behavior end to end.
public sealed class BradescoPixTests
{
    internal const string WebhookUrl = "https://app-under-test.local/webhooks/pix";

    [Fact]
    public async Task Happy_path_creates_pays_fires_webhook_and_journals()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();

        var created = await CreateCob(http, txid);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cob = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ATIVA", cob.GetProperty("status").GetString());
        Assert.Equal("123.45", cob.GetProperty("valor").GetProperty("original").GetString());

        var paid = await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        await factory.SettledAsync();
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(WebhookUrl, url);
        var pix = JsonDocument.Parse(body).RootElement.GetProperty("pix")[0];
        Assert.Equal(txid, pix.GetProperty("txid").GetString());
        Assert.StartsWith("E", pix.GetProperty("endToEndId").GetString());

        var fetched = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.Equal("CONCLUIDA", fetched.GetProperty("status").GetString());
        Assert.Equal(1, fetched.GetProperty("pix").GetArrayLength());

        var verify = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "OutboundWebhook" });
        Assert.Equal(1, verify.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Same_txid_is_idempotent()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();

        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, txid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CreateCob(http, txid)).StatusCode);

        var verify = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "InboundRequest", summaryContains = "PUT /bradesco/v2/cob/" });
        Assert.Equal(2, verify.GetProperty("count").GetInt32());
        Assert.Equal("idempotent hit", verify.GetProperty("matches")[1].GetProperty("detail").GetString());

        var transitions = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "StateTransition" });
        Assert.Equal(1, transitions.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Clock_advance_past_ttl_removes_charge()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        await http.PostAsJsonAsync("/_chameleon/clock/advance", new { seconds = 4000 });

        // The simulated token also expired (3600s), so re-authenticate before reading.
        http = await AuthorizedClient(factory);
        var fetched = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.Equal("REMOVIDA_PELO_PSP", fetched.GetProperty("status").GetString());
        Assert.Empty(factory.Webhooks.Received);
    }

    [Fact]
    public async Task Armed_fault_fails_next_call_only()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);

        (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Error", status = 503, times = 1 }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(http, NewTxId())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, NewTxId())).StatusCode);
    }

    [Fact]
    public async Task Rejects_missing_token_and_invalid_txid()
    {
        using var factory = new ChameleonFactory();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateCob(anonymous, NewTxId())).StatusCode);

        var http = await AuthorizedClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateCob(http, "curto")).StatusCode);
    }

    [Fact]
    public async Task Paying_a_removed_charge_conflicts()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/remove", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).StatusCode);
    }

    [Fact]
    public async Task Webhook_registered_for_pix_key_is_used()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        const string keyHook = "https://app-under-test.local/webhooks/pix-por-chave";

        Assert.Equal(HttpStatusCode.NoContent,
            (await http.PutAsJsonAsync($"/bradesco/v2/webhook/{Chave}", new { webhookUrl = keyHook })).StatusCode);
        var config = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/webhook/{Chave}");
        Assert.Equal(keyHook, config.GetProperty("webhookUrl").GetString());

        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);

        await factory.SettledAsync();
        var (url, _, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(keyHook, url);

        // Removed: the charge's own (simulator-only) URL applies again.
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/bradesco/v2/webhook/{Chave}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/bradesco/v2/webhook/{Chave}")).StatusCode);
        var other = NewTxId();
        await CreateCob(http, other);
        await http.PostAsync($"/_chameleon/bradesco/cob/{other}/pay", null);
        await factory.SettledAsync();
        Assert.Equal(WebhookUrl, factory.Webhooks.Received.Last().Url);
    }

    [Fact] // The operator sees and manages what the app registered, through the control API
    public async Task Webhook_registration_is_shared_by_provider_and_control_api()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        const string viaProvider = "https://app-under-test.local/webhooks/pela-api";
        const string viaControl = "https://app-under-test.local/webhooks/pelo-painel";

        await http.PutAsJsonAsync("/bradesco/v2/webhook/outra-chave", new { webhookUrl = viaProvider });
        var listed = Assert.Single((await http.GetFromJsonAsync<JsonElement>("/_chameleon/webhook-registrations")).EnumerateArray());
        Assert.Equal("bradesco", listed.GetProperty("connector").GetString());
        Assert.Equal("outra-chave", listed.GetProperty("key").GetString());
        Assert.Equal(viaProvider, listed.GetProperty("url").GetString());

        // Set by the operator: the provider API answers with it and the payment notifies it.
        var set = await http.PutAsJsonAsync($"/_chameleon/connectors/bradesco/webhook-registrations/{Chave}", new { url = viaControl });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(viaControl, (await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/webhook/{Chave}")).GetProperty("webhookUrl").GetString());
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();
        Assert.Equal(viaControl, Assert.Single(factory.Webhooks.Received).Url);

        var journaled = await Verify(http, new { connector = "bradesco", entityId = Chave, kind = "ControlAction" });
        Assert.Equal(1, journaled.GetProperty("count").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsJsonAsync($"/_chameleon/connectors/bradesco/webhook-registrations/{Chave}", new { url = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/_chameleon/connectors/bradesco/webhook-registrations/{Chave}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/_chameleon/connectors/bradesco/webhook-registrations/{Chave}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/bradesco/v2/webhook/{Chave}")).StatusCode);

        // A connector without registrations exposes none.
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("/_chameleon/webhook-registrations?connector=nfse-sp")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PutAsJsonAsync("/_chameleon/connectors/nfse-sp/webhook-registrations/x", new { url = viaControl })).StatusCode);
    }

    [Fact]
    public async Task Payment_without_webhook_still_transitions()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", new
        {
            calendario = new { expiracao = 3600 },
            valor = new { original = "10.00" },
            chave = Chave,
        });

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).StatusCode);
        Assert.Empty(factory.Webhooks.Received);

        var fetched = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.Equal("CONCLUIDA", fetched.GetProperty("status").GetString());
        var notSent = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "OutboundWebhook" });
        Assert.Equal($"no webhook registered for key {Chave}; no key with a webhook in this sandbox",
            notSent.GetProperty("matches")[0].GetProperty("detail").GetString());
    }

    [Fact] // the operator registered another key: the note names the cob's key and the registered ones, CPF masked
    public async Task Payment_to_an_unregistered_key_names_the_registered_keys()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        (await http.PutAsJsonAsync("/bradesco/v2/webhook/12345678901", new { webhookUrl = WebhookUrl })).EnsureSuccessStatusCode();
        var txid = NewTxId();
        await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", new { calendario = new { expiracao = 3600 }, valor = new { original = "10.00" }, chave = Chave });

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).StatusCode);

        var notSent = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "OutboundWebhook" });
        Assert.Equal($"no webhook registered for key {Chave}; keys with a webhook: 12*******01",
            notSent.GetProperty("matches")[0].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Webhook_config_requires_token_and_valid_url()
    {
        using var factory = new ChameleonFactory();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PutAsJsonAsync($"/bradesco/v2/webhook/{Chave}", new { webhookUrl = WebhookUrl })).StatusCode);

        var http = await AuthorizedClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsJsonAsync($"/bradesco/v2/webhook/{Chave}", new { webhookUrl = "ftp://x" })).StatusCode);
    }

    internal const string Chave = "00000000-0000-4000-8000-000000000001";

    internal static string NewTxId() => "chameleon" + Guid.NewGuid().ToString("N")[..22];

    internal static async Task<HttpClient> AuthorizedClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        var token = await TestAuth.BradescoToken(http, "tests", "s");
        token.EnsureSuccessStatusCode();
        var accessToken = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    internal static Task<HttpResponseMessage> CreateCob(HttpClient http, string txid) =>
        http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", new
        {
            calendario = new { expiracao = 3600 },
            devedor = new { cpf = "12345678909", nome = "Joao Silva" },
            valor = new { original = "123.45" },
            chave = Chave,
            webhookUrl = WebhookUrl
        });

    internal static async Task<JsonElement> Verify(HttpClient http, object query)
    {
        var resp = await http.PostAsJsonAsync("_chameleon/verify", query);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
