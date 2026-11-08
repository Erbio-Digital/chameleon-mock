using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco balance and statement as a client application parses them, and the four calls a typical
/// health check makes.
public sealed class BradescoContaTests
{
    [Fact] // GetBalance reads the "SALDO TOTAL" item, pt-BR value, sign apart
    public async Task Balance_is_saldo_total_in_pt_br()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-conta");
        await Bradesco.OpenAccount(http, "carteira", "0001", "0012345", null, 12345.67m);

        var saldo = await http.GetFromJsonAsync<JsonElement>("bradesco/v1/fornecimento-saldos-contas/saldos?agencia=0001&conta=0012345");

        var item = saldo.GetProperty("saldoCC").GetProperty("lstLancamentosSaldos")[0];
        Assert.Equal("SALDO TOTAL", item.GetProperty("nomeProduto").GetString());
        Assert.Equal("12.345,67", item.GetProperty("valorLancamento").GetString());
        Assert.Equal("+", item.GetProperty("sinalSaldo").GetString());
        Assert.Equal(JsonValueKind.Number, item.GetProperty("codigoProduto").ValueKind);
        (await http.GetAsync("bradesco/v1/fornecimento-saldos-contas/saldos?agencia=0001&conta=999")).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact] // tipoLancamento 2/9; each entry in one list only (clients concatenate them)
    public async Task Statement_lists_today_and_past_days_apart()
    {
        var (_, http, sandbox) = await Bradesco.Sandbox("ci-conta");
        await Bradesco.OpenAccount(http, "carteira", "0001", "0012345", BradescoPixTests.Chave, 100);
        var txid = BradescoPixTests.NewTxId();
        await BradescoPixTests.CreateCob(http, txid);
        (await Bradesco.Action(http, "cob", txid, "pay", new { amount = "20.00" })).AssertStatus(HttpStatusCode.OK);
        var e2e = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0].GetProperty("endToEndId").GetString();
        (await http.PutAsJsonAsync($"bradesco/v2/pix/{e2e}/devolucao/{Guid.NewGuid():N}", new { valor = "5.00" })).AssertStatus(HttpStatusCode.Created);
        var today = DateTime.UtcNow.AddHours(-3);

        var extrato = await Statement(http, today, today);

        var dia = extrato.GetProperty("extratoUltimosLancamentos").GetProperty("listaLancamentos").GetProperty("Lancamentos Dia").EnumerateArray().ToList();
        Assert.Equal(["2", "2", "9"], dia.Select(i => i.GetProperty("tipoLancamento").GetString()));
        Assert.Equal(["+", "+", "-"], dia.Select(i => i.GetProperty("sinalLancamento").GetString()));
        Assert.Equal("20,00", dia[1].GetProperty("valorLancamento").GetString());
        Assert.Equal("115,00", dia[2].GetProperty("valorSaldoAposLancamento").GetString());
        Assert.Equal(today.ToString("dd/MM/yyyy"), dia[0].GetProperty("dataLancamento").GetString());
        Assert.Empty(extrato.GetProperty("extratoPorPeriodo").GetProperty("lstLancamentoMensal").EnumerateArray());

        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 86400 })).EnsureSuccessStatusCode();
        await TestAuth.Bradesco(http, sandbox);
        var depois = await Statement(http, today, today.AddDays(1));
        Assert.Equal(3, depois.GetProperty("extratoPorPeriodo").GetProperty("lstLancamentoMensal").GetArrayLength());
        Assert.Empty(depois.GetProperty("extratoUltimosLancamentos").GetProperty("listaLancamentos").GetProperty("Lancamentos Dia").EnumerateArray());

        var saldoAnterior = (await Statement(http, today.AddDays(1), today.AddDays(1)))
            .GetProperty("extratoUltimosLancamentos").GetProperty("listaLancamentos").GetProperty("Saldo Anterior")[0];
        Assert.Equal("115,00", saldoAnterior.GetProperty("valorLancamento").GetString());
    }

    [Fact]
    public async Task Statement_needs_a_valid_period()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-conta");
        await Bradesco.OpenAccount(http, "carteira", "0001", "0012345", null, 0);

        (await http.GetAsync("bradesco/v1/fornecimento-extratos-contas/extratos?dataInicio=2026-10-01&dataFim=12102026&agencia=0001&conta=0012345&tipo=cc"))
            .AssertStatus(HttpStatusCode.BadRequest);
        (await http.GetAsync("bradesco/v1/fornecimento-extratos-contas/extratos?dataInicio=12102026&dataFim=01102026&agencia=0001&conta=0012345&tipo=cc"))
            .AssertStatus(HttpStatusCode.BadRequest);
    }

    [Fact] // A typical health check: 400/404/422 = authenticated; the balance of the WALLET account = 200
    public async Task Health_check_passes_without_setup()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-health");
        await Bradesco.OpenAccount(http, "wallet", "0001", "0012345", null, 0);
        var issued = await TestAuth.Subscribe(http, "bradesco");
        var now = DateTime.UtcNow;

        // QRCODEPIX and TRANSFER with the Basic token already in the client.
        var cob = await http.GetAsync($"bradesco/v2/cob/{$"HEALTHCHECK{Guid.NewGuid():N}"[..31].ToUpperInvariant()}");
        var spi = await http.GetAsync($"bradesco/v1/spi/consulta/transferencia/{Guid.NewGuid():N}/E60746948{now:yyyyMMddHHmm}HEALTHCHECK");

        // TED and ACCOUNT with the form token of auth/server-mtls/v2/token.
        var form = await http.PostAsync("bradesco/auth/server-mtls/v2/token", new FormUrlEncodedContent([
            new("grant_type", "client_credentials"), new("client_id", issued["CLIENT_ID"]), new("client_secret", issued["CLIENT_SECRET"])]));
        form.AssertStatus(HttpStatusCode.OK);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await form.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        var ted = await http.GetAsync($"bradesco/transferencia/ted/v1/consulta?numeroDocumento=1&dataOperacao={now.AddHours(-3):dd.MM.yyyy}");
        var saldo = await http.GetAsync("bradesco/v1/fornecimento-saldos-contas/saldos?agencia=0001&conta=0012345");

        cob.AssertStatus(HttpStatusCode.NotFound);
        spi.AssertStatus(HttpStatusCode.NotFound);
        ted.AssertStatus(HttpStatusCode.UnprocessableEntity);
        saldo.AssertStatus(HttpStatusCode.OK);
    }

    private static Task<JsonElement> Statement(HttpClient http, DateTime inicio, DateTime fim) =>
        http.GetFromJsonAsync<JsonElement>(
            $"bradesco/v1/fornecimento-extratos-contas/extratos?dataInicio={inicio:ddMMyyyy}&dataFim={fim:ddMMyyyy}&agencia=0001&conta=0012345&tipo=cc");
}
