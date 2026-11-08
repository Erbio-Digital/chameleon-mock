using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco SPI (outgoing Pix), with a full body and a key-only body (tipoChave/chavePix), and the
/// refusals a client application reads (400 on ".recebedor." or 412 "chave ... DICT").
public sealed class BradescoSpiTests
{
    private const string Evp = "9f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f";

    [Fact] // sync: CONCLUIDO with e2e, debit and credit in the sandbox
    public async Task Sync_transfer_concludes_and_moves_money()
    {
        var http = await Accounts();

        var resp = await Transfer(http, Id(), FullBody(Evp));

        resp.AssertStatus(HttpStatusCode.OK);
        var t = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONCLUIDO", t.GetProperty("status").GetString());
        Assert.Equal("0.00", t.GetProperty("valorTarifa").GetString());
        var e2e = t.GetProperty("e2e").GetString()!;
        Assert.Matches("^E60746948[0-9]{12}[0-9A-F]{11}$", e2e);
        Assert.Equal("20.00", await Bradesco.Balance(http, "pagadora"));
        Assert.Equal("80.00", await Bradesco.Balance(http, "recebedora"));

        var consulta = await http.GetFromJsonAsync<JsonElement>($"bradesco/v1/spi/consulta/transferencia/{t.GetProperty("idTransacao").GetString()}/{e2e}");
        Assert.Equal("80.00", consulta.GetProperty("valor").GetString());
        Assert.Equal(Evp, consulta.GetProperty("recebedor").GetProperty("chavePix").GetString());
        Assert.Equal(JsonValueKind.String, consulta.GetProperty("dataEfetivacao").ValueKind);
    }

    [Fact] // A key outside the sandbox DICT -> 412, a definitive refusal
    public async Task Unknown_key_is_precondition_failed_with_dict()
    {
        var http = await Accounts();

        var resp = await Transfer(http, Id(), FullBody("0a0a0a0a-0000-4000-8000-000000000000"));

        resp.AssertStatus(HttpStatusCode.PreconditionFailed);
        var detail = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("chave", detail);
        Assert.Contains("DICT", detail);
        Assert.Equal("100.00", await Bradesco.Balance(http, "pagadora"));
    }

    [Fact] // A CPF sent as TELEFONE
    public async Task Cpf_sent_as_telefone_is_refused_on_recebedor_tipo_chave()
    {
        var http = await Accounts();

        var resp = await Transfer(http, Id(), new { tipoChave = "TELEFONE", chavePix = "12345678909" });

        resp.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal("solicitacao.recebedor.tipoChave", Propriedade(await resp.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact] // The document must be the key holder's
    public async Task Other_document_for_a_cpf_key_is_refused()
    {
        var http = await Accounts();

        var resp = await Transfer(http, Id(), new { tipoChave = "CPFCNPJ", chavePix = "12345678909", cpfCnpj = "98765432100", nomeFavorecido = "Maria" });

        resp.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal("solicitacao.recebedor.cpfCnpj", Propriedade(await resp.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact] // The same idtransacao never pays twice, and its 412 is not a DICT refusal
    public async Task Repeated_idtransacao_is_duplicate()
    {
        var http = await Accounts();
        var id = Id();
        (await Transfer(http, id, FullBody(Evp))).AssertStatus(HttpStatusCode.OK);

        var again = await Transfer(http, id, FullBody(Evp));

        again.AssertStatus(HttpStatusCode.PreconditionFailed);
        Assert.DoesNotContain("DICT", await again.Content.ReadAsStringAsync());
        Assert.Equal("20.00", await Bradesco.Balance(http, "pagadora"));
    }

    [Fact] // async: no e2e until an action; clients query with an empty e2e (trailing slash)
    public async Task Async_transfer_waits_and_rejection_has_an_e2e()
    {
        var http = await Accounts();
        (await http.PutAsJsonAsync("_chameleon/connectors/bradesco/subscription/settings",
            new Dictionary<string, string> { ["SPI_MODE"] = "async" })).EnsureSuccessStatusCode();
        var (concluded, rejected) = (Id(), Id());

        var resp = await Transfer(http, concluded, FullBody(Evp), "30.00");
        resp.AssertStatus(HttpStatusCode.OK);
        var t = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EM_PROCESSAMENTO", t.GetProperty("status").GetString());
        Assert.False(t.TryGetProperty("e2e", out _));
        var pending = await http.GetFromJsonAsync<JsonElement>($"bradesco/v1/spi/consulta/transferencia/{concluded}/");
        Assert.Equal("EM_PROCESSAMENTO", pending.GetProperty("status").GetString());

        (await Bradesco.Action(http, "transferencia-pix", concluded, "conclude", new { })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("CONCLUIDO", (await http.GetFromJsonAsync<JsonElement>($"bradesco/v1/spi/consulta/transferencia/{concluded}/")).GetProperty("status").GetString());
        Assert.Equal("70.00", await Bradesco.Balance(http, "pagadora"));

        // A client that confirms by the e2e alone: a rejection carries one.
        (await Transfer(http, rejected, FullBody(Evp), "30.00")).AssertStatus(HttpStatusCode.OK);
        (await Bradesco.Action(http, "transferencia-pix", rejected, "reject", new { reason = "Destination account closed", reasonCode = "AC04" }))
            .AssertStatus(HttpStatusCode.OK);
        var rejection = await http.GetFromJsonAsync<JsonElement>($"bradesco/v1/spi/consulta/transferencia/{rejected}/");
        Assert.Equal("REJEITADO", rejection.GetProperty("status").GetString());
        Assert.Equal("Destination account closed", rejection.GetProperty("motivo").GetString());
        Assert.StartsWith("E60746948", rejection.GetProperty("e2e").GetString());
        Assert.Equal("70.00", await Bradesco.Balance(http, "pagadora"));
    }

    [Fact] // Accepted and REJEITADO for lack of balance, never sent (no e2e)
    public async Task Insufficient_balance_is_rejected()
    {
        var http = await Accounts();

        var resp = await Transfer(http, Id(), FullBody(Evp), "100.01");

        resp.AssertStatus(HttpStatusCode.OK);
        var t = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REJEITADO", t.GetProperty("status").GetString());
        Assert.Equal("SALDO INSUFICIENTE", t.GetProperty("motivo").GetString());
        Assert.False(t.TryGetProperty("e2e", out _));
    }

    [Fact] // Health check: an unknown transfer is 404, which reads as authenticated
    public async Task Unknown_transfer_is_not_found_and_journal_hides_bank_details()
    {
        var http = await Accounts();
        (await Transfer(http, Id(), FullBody(Evp))).AssertStatus(HttpStatusCode.OK);

        (await http.GetAsync($"bradesco/v1/spi/consulta/transferencia/{Id()}/E60746948{DateTime.UtcNow:yyyyMMddHHmm}HEALTHCHECK"))
            .AssertStatus(HttpStatusCode.NotFound);

        var journal = await http.GetStringAsync("_chameleon/journal?connector=bradesco&kind=InboundRequest");
        Assert.DoesNotContain("12345678909", journal);
        Assert.DoesNotContain("0012345", journal);
    }

    /// Payer at the Bradesco with 100.00; receiver of the EVP key with nothing.
    private static async Task<HttpClient> Accounts()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-spi");
        await Bradesco.OpenAccount(http, "pagadora", "0001", "0012345", null, 100);
        await Bradesco.OpenAccount(http, "recebedora", "1234", "98765", Evp, 0);
        return http;
    }

    private static object FullBody(string chave) =>
        new { tipoChave = "EVP", chavePix = chave, cpfCnpj = "12345678909", nomeFavorecido = "Maria Souza" };

    private static Task<HttpResponseMessage> Transfer(HttpClient http, string id, object recebedor, string valor = "80.00") =>
        http.PostAsJsonAsync("bradesco/v1/spi/solicitar-transferencia", new
        {
            idtransacao = id,
            pagador = new { tipoChave = "AGENCIACONTA", agencia = "0001", conta = "0012345" },
            recebedor,
            valor,
            descricao = "Repasse",
        });

    private static string Id() => Guid.NewGuid().ToString("N");

    private static string? Propriedade(JsonElement problem) => problem.GetProperty("violacoes")[0].GetProperty("propriedade").GetString();
}
