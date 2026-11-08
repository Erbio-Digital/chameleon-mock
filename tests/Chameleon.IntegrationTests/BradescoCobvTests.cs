using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bacen cobv at the Bradesco: the Bacen schema decides, so a non-conforming body is refused;
/// a Bacen body creates a charge that expires by the clock.
public sealed class BradescoCobvTests
{
    [Fact] // dataDeVencimento "", nulls, valor.original as a number, no devedor
    public async Task Non_conforming_body_is_refused_with_violations()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cobv");

        // A model serialized with JsonSerializer.Serialize, as a client may send it.
        var resp = await http.PutAsync($"bradesco/v2/cobv/{BradescoPixTests.NewTxId()}", new StringContent("""
            {"chave":"00000000-0000-4000-8000-000000000001","calendario":{"dataDeVencimento":"","validadeAposVencimento":null},
             "valor":{"original":20.0,"desconto":null,"multa":null,"juros":null,"abatimento":null},"solicitacaoPagador":"Acme"}
            """, System.Text.Encoding.UTF8, "application/json"));

        resp.AssertStatus(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.EndsWith("/CobVOperacaoInvalida", problem.GetProperty("type").GetString());
        var props = problem.GetProperty("violacoes").EnumerateArray().Select(v => v.GetProperty("propriedade").GetString()).ToList();
        Assert.Equal(["calendario.dataDeVencimento", "devedor", "valor.original"], props);
    }

    [Fact] // Payable through the validity days, removed by the PSP at the next midnight (Brasilia)
    public async Task Bacen_body_creates_and_expires_by_the_clock()
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-cobv");
        var txid = BradescoPixTests.NewTxId();

        var created = await CreateCobV(http, txid, validade: 0);

        created.AssertStatus(HttpStatusCode.Created);
        var cobv = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Today, cobv.GetProperty("calendario").GetProperty("dataDeVencimento").GetString());
        Assert.Equal("ATIVA", cobv.GetProperty("status").GetString());
        Assert.Equal("50.00", cobv.GetProperty("valor").GetProperty("original").GetString());
        Assert.Equal(2, cobv.GetProperty("valor").GetProperty("multa").GetProperty("modalidade").GetInt32());
        Assert.Equal("12345678909", cobv.GetProperty("devedor").GetProperty("cpf").GetString());
        (await http.GetAsync($"bradesco/v2/cob/{txid}")).AssertStatus(HttpStatusCode.NotFound);
        (await http.GetAsync($"bradesco/v2/cobv/{txid}")).AssertStatus(HttpStatusCode.OK);

        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 2 * 86400 })).EnsureSuccessStatusCode();
        await TestAuth.Bradesco(http, sandbox); // the token expired with the clock
        Assert.Equal("REMOVIDA_PELO_PSP", (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cobv/{txid}")).GetProperty("status").GetString());
        await factory.SettledAsync(sandbox);
        Assert.Empty(factory.Webhooks.Received);
    }

    [Fact]
    public async Task Paid_cobv_notifies_the_key_webhook()
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-cobv");
        var txid = BradescoPixTests.NewTxId();
        (await CreateCobV(http, txid, validade: 30)).AssertStatus(HttpStatusCode.Created);

        (await Bradesco.Action(http, "cob", txid, "pay", new { })).AssertStatus(HttpStatusCode.OK);

        var cobv = await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cobv/{txid}");
        Assert.Equal("CONCLUIDA", cobv.GetProperty("status").GetString());
        Assert.Equal("50.00", cobv.GetProperty("pix")[0].GetProperty("valor").GetString());
        await factory.SettledAsync(sandbox);
        Assert.Equal(txid, JsonDocument.Parse(Assert.Single(factory.Webhooks.Received).Body).RootElement.GetProperty("pix")[0].GetProperty("txid").GetString());
    }

    [Fact]
    public async Task Txid_of_a_cob_cannot_become_a_cobv()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-cobv");
        var txid = BradescoPixTests.NewTxId();
        await BradescoPixTests.CreateCob(http, txid);

        (await CreateCobV(http, txid, validade: 0)).AssertStatus(HttpStatusCode.BadRequest);
        (await http.GetAsync($"bradesco/v2/cobv/{txid}")).AssertStatus(HttpStatusCode.NotFound);
    }

    private static string Today => DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-dd");

    private static Task<HttpResponseMessage> CreateCobV(HttpClient http, string txid, int validade) =>
        http.PutAsJsonAsync($"bradesco/v2/cobv/{txid}", new
        {
            calendario = new { dataDeVencimento = Today, validadeAposVencimento = validade },
            devedor = new { cpf = "12345678909", nome = "Joao Silva" },
            valor = new { original = "50.00", multa = new { modalidade = 2, valorPerc = "2.00" } },
            chave = BradescoPixTests.Chave,
            solicitacaoPagador = "Acme",
        });
}
