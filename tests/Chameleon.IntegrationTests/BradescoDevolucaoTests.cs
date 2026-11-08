using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bacen devolucao at the Bradesco, as a client application calls it:
/// PUT v2/pix/{e2eid}/devolucao/{Guid N} with valor "10.00"; on 412, GET the existing one.
public sealed class BradescoDevolucaoTests
{
    [Fact] // sync: DEVOLVIDO in the response, debit, devolucoes[] in the cob and in the webhook
    public async Task Sync_refund_debits_and_notifies_devolucoes()
    {
        var (factory, http, sandbox, txid, e2e) = await PaidPix("20.00");

        var resp = await Refund(http, e2e, Id(), "5.00");

        resp.AssertStatus(HttpStatusCode.Created);
        var devolucao = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEVOLVIDO", devolucao.GetProperty("status").GetString());
        Assert.Equal("5.00", devolucao.GetProperty("valor").GetString());
        Assert.Matches("^D60746948[0-9]{12}[0-9A-F]{11}$", devolucao.GetProperty("rtrId").GetString());
        Assert.Equal("15.00", await Bradesco.Balance(http, "recebedora"));

        var pix = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0];
        Assert.Equal("DEVOLVIDO", pix.GetProperty("devolucoes")[0].GetProperty("status").GetString());

        await factory.SettledAsync(sandbox);
        var notified = JsonDocument.Parse(factory.Webhooks.Received.Last().Body).RootElement.GetProperty("pix")[0];
        Assert.Equal(e2e, notified.GetProperty("endToEndId").GetString());
        Assert.Equal("5.00", notified.GetProperty("devolucoes")[0].GetProperty("valor").GetString());
    }

    [Fact] // Same id -> 412; the client then GETs the refund
    public async Task Repeated_id_is_precondition_failed_and_get_finds_it()
    {
        var (_, http, _, _, e2e) = await PaidPix("20.00");
        var id = Id();
        (await Refund(http, e2e, id, "5.00")).AssertStatus(HttpStatusCode.Created);

        (await Refund(http, e2e, id, "5.00")).AssertStatus(HttpStatusCode.PreconditionFailed);

        var existing = await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/pix/{e2e}/devolucao/{id}");
        Assert.Equal("DEVOLVIDO", existing.GetProperty("status").GetString());
        Assert.Equal("15.00", await Bradesco.Balance(http, "recebedora"));
    }

    [Fact] // valor pt-BR and above the refundable amount
    public async Task Comma_valor_and_excess_are_refused()
    {
        var (_, http, _, _, e2e) = await PaidPix("20.00");

        var comma = await Refund(http, e2e, Id(), "10,00");
        comma.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal("valor", (await comma.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("violacoes")[0].GetProperty("propriedade").GetString());

        (await Refund(http, e2e, Id(), "15.00")).AssertStatus(HttpStatusCode.Created);
        (await Refund(http, e2e, Id(), "5.01")).AssertStatus(HttpStatusCode.BadRequest);
        (await Refund(http, e2e, Id(), "5.00")).AssertStatus(HttpStatusCode.Created);
    }

    [Fact] // async: EM_PROCESSAMENTO until an action settles or refuses
    public async Task Async_refund_waits_for_an_action()
    {
        var (_, http, _, _, e2e) = await PaidPix("20.00");
        (await http.PutAsJsonAsync("_chameleon/connectors/bradesco/subscription/settings",
            new Dictionary<string, string> { ["REFUND_MODE"] = "async" })).EnsureSuccessStatusCode();
        var (settled, refused) = (Id(), Id());

        var resp = await Refund(http, e2e, settled, "5.00");
        resp.AssertStatus(HttpStatusCode.Created);
        Assert.Equal("EM_PROCESSAMENTO", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("20.00", await Bradesco.Balance(http, "recebedora"));

        (await Bradesco.Action(http, "devolucao", $"{e2e}_{settled}", "settle", new { })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("15.00", await Bradesco.Balance(http, "recebedora"));

        (await Refund(http, e2e, refused, "5.00")).AssertStatus(HttpStatusCode.Created);
        (await Bradesco.Action(http, "devolucao", $"{e2e}_{refused}", "reject", new { reason = "account closed" })).AssertStatus(HttpStatusCode.OK);
        var naoRealizada = await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/pix/{e2e}/devolucao/{refused}");
        Assert.Equal("NAO_REALIZADO", naoRealizada.GetProperty("status").GetString());
        Assert.Equal("account closed", naoRealizada.GetProperty("motivo").GetString());
        Assert.Equal("15.00", await Bradesco.Balance(http, "recebedora"));
    }

    [Fact]
    public async Task Refund_without_balance_is_not_made()
    {
        var (_, http, _, _, e2e) = await PaidPix("20.00");
        (await http.PutAsJsonAsync("_chameleon/connectors/bradesco/accounts/recebedora/balance", new { balance = 1m })).EnsureSuccessStatusCode();

        var resp = await Refund(http, e2e, Id(), "5.00");

        resp.AssertStatus(HttpStatusCode.Created);
        var devolucao = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NAO_REALIZADO", devolucao.GetProperty("status").GetString());
        Assert.Equal("SALDO INSUFICIENTE", devolucao.GetProperty("motivo").GetString());
    }

    [Fact] // A call to pix/{e2e}/devolucao/{id} without v2 -> 404, visible in the feed; unknown e2e -> 404
    public async Task Path_without_v2_and_unknown_pix_are_not_found()
    {
        var (_, http, _, _, e2e) = await PaidPix("20.00");

        (await Refund(http, e2e, Id(), "5.00", path: "bradesco/pix")).AssertStatus(HttpStatusCode.NotFound);
        (await Refund(http, "E00000000202601010000ABCDEFGHIJK", Id(), "5.00")).AssertStatus(HttpStatusCode.NotFound);

        var journal = (await http.GetFromJsonAsync<JsonElement>("_chameleon/journal?connector=bradesco&kind=InboundRequest")).EnumerateArray();
        Assert.Contains(journal, e => e.GetProperty("path").GetString()!.StartsWith("/bradesco/pix/") && e.GetProperty("statusCode").GetInt32() == 404);
    }

    /// A paid 32-hex charge credited to the "recebedora" account (the test key's owner).
    private static async Task<(ChameleonFactory, HttpClient, string, string Txid, string E2e)> PaidPix(string valor)
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-dev");
        await Bradesco.OpenAccount(http, "recebedora", "0001", "0012345", BradescoPixTests.Chave, 0);
        var txid = BradescoCobTests.ClientTxId();
        (await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", BradescoCobTests.ClientBody(valor))).AssertStatus(HttpStatusCode.Created);
        (await Bradesco.Action(http, "cob", txid, "pay", new { })).AssertStatus(HttpStatusCode.OK);
        var e2e = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0].GetProperty("endToEndId").GetString()!;
        return (factory, http, sandbox, txid, e2e);
    }

    private static string Id() => Guid.NewGuid().ToString("N");

    private static Task<HttpResponseMessage> Refund(HttpClient http, string e2e, string id, string valor, string path = "bradesco/v2/pix") =>
        http.PutAsJsonAsync($"{path}/{e2e}/devolucao/{id}", new { valor });
}
