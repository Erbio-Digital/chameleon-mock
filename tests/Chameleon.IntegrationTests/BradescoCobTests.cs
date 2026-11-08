using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco cob: the paid amount, the payer and infoPagador in pix[] and in the Bacen webhook,
/// with strict response types like a client application parses.
public sealed class BradescoCobTests
{
    [Fact] // A client body: 32 lowercase hex txid and valor.modalidadeAlteracao 0
    public async Task Client_body_creates_with_strict_types()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cob");

        var created = await http.PutAsJsonAsync($"bradesco/v2/cob/{ClientTxId()}", ClientBody("20.00"));

        created.AssertStatus(HttpStatusCode.Created);
        var cob = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, cob.GetProperty("revisao").ValueKind);
        Assert.Equal(JsonValueKind.Number, cob.GetProperty("calendario").GetProperty("expiracao").ValueKind);
        Assert.Equal("20.00", cob.GetProperty("valor").GetProperty("original").GetString());
        Assert.Equal(0, cob.GetProperty("valor").GetProperty("modalidadeAlteracao").GetInt32());
        Assert.Empty(cob.GetProperty("pix").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_modalidade_alteracao_is_refused()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cob");

        var created = await http.PutAsJsonAsync($"bradesco/v2/cob/{ClientTxId()}", new
        {
            calendario = new { expiracao = 1800 }, valor = new { original = "20.00", modalidadeAlteracao = 2 }, chave = BradescoPixTests.Chave,
        });

        created.AssertStatus(HttpStatusCode.BadRequest);
        var problem = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("valor.modalidadeAlteracao", problem.GetProperty("violacoes")[0].GetProperty("propriedade").GetString());
    }

    [Fact] // Detecting an amount mismatch and checking the payer document need both in pix[]
    public async Task Pay_with_divergent_amount_and_payer_shows_in_cob_and_webhook()
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-cob");
        var txid = ClientTxId();
        (await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", ClientBody("20.00"))).AssertStatus(HttpStatusCode.Created);

        (await Bradesco.Action(http, "cob", txid, "pay",
            new { amount = "15.00", payerDocument = "123.456.789-09", payerName = "Maria Souza", payerInfo = "order 42" }))
            .AssertStatus(HttpStatusCode.OK);

        var pix = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0];
        Assert.Equal("15.00", pix.GetProperty("valor").GetString());
        Assert.Equal("12345678909", pix.GetProperty("pagador").GetProperty("cpf").GetString());
        Assert.Equal("Maria Souza", pix.GetProperty("pagador").GetProperty("nome").GetString());
        Assert.Equal("order 42", pix.GetProperty("infoPagador").GetString());

        await factory.SettledAsync(sandbox);
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(BradescoPixTests.WebhookUrl, url);
        var sent = JsonDocument.Parse(body).RootElement.GetProperty("pix")[0];
        Assert.Equal(txid, sent.GetProperty("txid").GetString());
        Assert.Equal("15.00", sent.GetProperty("valor").GetString());
        Assert.Equal("15.00", sent.GetProperty("componentesValor").GetProperty("original").GetProperty("valor").GetString());
        Assert.Equal("12345678909", sent.GetProperty("pagador").GetProperty("cpf").GetString());
        Assert.Equal("order 42", sent.GetProperty("infoPagador").GetString());

        // LGPD: the payer's document reaches the app, never the journal in clear.
        Assert.DoesNotContain("12345678909", await http.GetStringAsync("_chameleon/journal?connector=bradesco"));
    }

    [Fact] // without arguments: the original amount and the debtor as payer, no infoPagador
    public async Task Pay_without_arguments_uses_original_and_debtor()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cob");
        var txid = BradescoPixTests.NewTxId();
        await BradescoPixTests.CreateCob(http, txid);

        (await Bradesco.Action(http, "cob", txid, "pay", new { })).AssertStatus(HttpStatusCode.OK);

        var pix = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0];
        Assert.Equal("123.45", pix.GetProperty("valor").GetString());
        Assert.Equal("Joao Silva", pix.GetProperty("pagador").GetProperty("nome").GetString());
        Assert.False(pix.TryGetProperty("infoPagador", out _));
    }

    [Fact]
    public async Task Pay_with_invalid_document_is_refused_and_keeps_the_charge()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cob");
        var txid = ClientTxId();
        await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", ClientBody("20.00"));

        (await Bradesco.Action(http, "cob", txid, "pay", new { payerDocument = "123" })).AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal("ATIVA", (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("status").GetString());
    }

    [Fact] // the activation a receiver accepts ({"pix":[]} -> 2xx), never a charge the app does not know
    public async Task Test_event_is_an_empty_pix_list()
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-cob");

        (await http.PostAsync("_chameleon/connectors/bradesco/subscription/test-event", null)).AssertStatus(HttpStatusCode.Accepted);

        await factory.SettledAsync(sandbox);
        Assert.Equal("""{"pix":[]}""", Assert.Single(factory.Webhooks.Received).Body);
    }

    internal static string ClientTxId() => Guid.NewGuid().ToString("N");

    internal static object ClientBody(string valor) => new
    {
        chave = BradescoPixTests.Chave,
        calendario = new { expiracao = 1800 },
        valor = new { original = valor, modalidadeAlteracao = 0 },
        solicitacaoPagador = "Recarga de carteira",
    };
}

/// A subscribed sandbox with a Bradesco bearer and the test key's webhook registered.
internal static class Bradesco
{
    internal static async Task<(ChameleonFactory Factory, HttpClient Http, string Sandbox)> Sandbox(string prefix)
    {
        var factory = new ChameleonFactory();
        var sandbox = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        await TestAuth.Bradesco(http, sandbox);
        return (factory, http, sandbox);
    }

    internal static Task<HttpResponseMessage> Action(HttpClient http, string type, string key, string action, object args) =>
        http.PostAsJsonAsync($"_chameleon/connectors/bradesco/entities/{type}/{key}/actions/{action}", args);

    internal static async Task<JsonElement> OpenAccount(HttpClient http, string nickname, string branch, string account, string? pixKey, decimal balance)
    {
        var resp = await http.PostAsJsonAsync("_chameleon/connectors/bradesco/accounts", new { nickname, branch, account, pixKey, balance });
        resp.AssertStatus(HttpStatusCode.Created);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static async Task<string?> Balance(HttpClient http, string nickname) =>
        (await http.GetFromJsonAsync<JsonElement>($"_chameleon/accounts/{nickname}")).GetProperty("balance").GetString();
}
