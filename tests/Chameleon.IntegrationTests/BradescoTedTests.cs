using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Bradesco TED with a typical transfer body and the status query
/// (numeroDocumento = int of the first 7 chars of chaveUnicaParaApi).
public sealed class BradescoTedTests
{
    [Fact] // efetiva codigoDeRetorno 23 (number), query codigoRetorno (string) and "EM PROCESSAMENTO"
    public async Task Efetiva_debits_and_query_shows_it_in_processing()
    {
        var http = await Accounts(TedManual);

        var resp = await Efetiva(http, Codigo(), 150.75m);

        resp.AssertStatus(HttpStatusCode.OK);
        var ted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, ted.GetProperty("codigoDeRetorno").ValueKind);
        Assert.Equal(23, ted.GetProperty("codigoDeRetorno").GetInt32());
        var chave = ted.GetProperty("chaveUnicaParaApi").GetString()!;
        Assert.Matches("^[1-9][0-9]{6}[0-9A-F]{5}$", chave);
        Assert.Equal("849.25", await Bradesco.Balance(http, "remetente"));

        var consulta = await Consulta(http, chave);
        consulta.AssertStatus(HttpStatusCode.OK);
        var body = await consulta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, body.GetProperty("codigoRetorno").ValueKind);
        Assert.Equal("EM PROCESSAMENTO", body.GetProperty("statusMensagem").GetString());
        Assert.False(body.TryGetProperty("codigoDaDevolucao", out _));
    }

    [Fact] // TED_MODE automatic (default): processed with the efetiva, as clients poll for it
    public async Task Automatic_mode_processes_on_efetiva()
    {
        var http = await Accounts();

        var resp = await Efetiva(http, Codigo(), 150.75m);

        var ted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(23, ted.GetProperty("codigoDeRetorno").GetInt32());
        var chave = ted.GetProperty("chaveUnicaParaApi").GetString()!;
        Assert.Equal("PROCESSADA", (await (await Consulta(http, chave)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusMensagem").GetString());
        Assert.Equal("849.25", await Bradesco.Balance(http, "remetente"));
        Assert.Equal("150.75", await Bradesco.Balance(http, "destino"));
    }

    [Fact]
    public async Task Processar_credits_the_destination()
    {
        var http = await Accounts(TedManual);
        var chave = await Sent(http, 150.75m);

        (await Bradesco.Action(http, "ted", Key(chave), "process", new { })).AssertStatus(HttpStatusCode.OK);

        Assert.Equal("PROCESSADA", (await (await Consulta(http, chave)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusMensagem").GetString());
        Assert.Equal("150.75", await Bradesco.Balance(http, "destino"));
    }

    [Fact] // codigoDaDevolucao is a number, as the bank manual shows and clients read it (int?)
    public async Task Devolver_returns_the_money_with_a_numeric_code()
    {
        var http = await Accounts(TedManual);
        var chave = await Sent(http, 150.75m);

        (await Bradesco.Action(http, "ted", Key(chave), "return", new { returnCode = 2, returnDescription = "AG.OU CTA DEST. INVALIDA" }))
            .AssertStatus(HttpStatusCode.OK);

        var body = await (await Consulta(http, chave)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEVOLVIDA", body.GetProperty("statusMensagem").GetString());
        Assert.Equal(2, body.GetProperty("codigoDaDevolucao").GetInt32());
        Assert.Equal("AG.OU CTA DEST. INVALIDA", body.GetProperty("descricaoDaDevolucao").GetString());
        Assert.Equal("1000.00", await Bradesco.Balance(http, "remetente"));
    }

    [Fact] // Unknown document -> 422 (a health check queries "0000001" for today)
    public async Task Unknown_document_is_unprocessable()
    {
        var http = await Accounts();

        (await Consulta(http, "0000001")).AssertStatus(HttpStatusCode.UnprocessableEntity);
    }

    [Fact] // The same codigoIdentificadorDaTransferencia is the same TED, debited once
    public async Task Same_identifier_is_the_same_ted()
    {
        var http = await Accounts();
        var codigo = Codigo();
        var first = (await (await Efetiva(http, codigo, 100m)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chaveUnicaParaApi").GetString();

        var again = (await (await Efetiva(http, codigo, 100m)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chaveUnicaParaApi").GetString();

        Assert.Equal(first, again);
        Assert.Equal("900.00", await Bradesco.Balance(http, "remetente"));
    }

    [Fact] // business refusals are 200 with codigoDeRetorno != 23 and mensagem; malformed bodies are 422
    public async Task Refusals_follow_the_bank_contract()
    {
        var http = await Accounts();

        var noBalance = await (await Efetiva(http, Codigo(), 1000.01m)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(82, noBalance.GetProperty("codigoDeRetorno").GetInt32());
        Assert.Equal("SALDO INSUFICIENTE", noBalance.GetProperty("mensagem").GetString());

        var unknown = await (await Efetiva(http, Codigo(), 10m, contaRemetente: 999999)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, unknown.GetProperty("codigoDeRetorno").GetInt32());

        var malformed = await Efetiva(http, Codigo(), 10m, numeroFilial: "000024");
        malformed.AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("numeroFilial", (await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mensagem").GetString());
        Assert.Equal("1000.00", await Bradesco.Balance(http, "remetente"));
    }

    private static string Today => DateTime.UtcNow.AddHours(-3).ToString("dd.MM.yyyy");

    private static string Key(string chave) => $"{int.Parse(chave[..7])}-{DateTime.UtcNow.AddHours(-3):yyyyMMdd}";

    private static string Codigo() => Guid.NewGuid().ToString("N").ToUpperInvariant()[..25];

    private const string TedManual = "manual";

    /// Sender at the Bradesco with 1000.00 (branch 0001, account 0012345); destination account 98765 at branch 1234.
    private static async Task<HttpClient> Accounts(string? mode = null)
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-ted");
        if (mode is not null)
            (await http.PutAsJsonAsync("_chameleon/connectors/bradesco/subscription/settings",
                new Dictionary<string, string> { ["TED_MODE"] = mode })).EnsureSuccessStatusCode();
        await Bradesco.OpenAccount(http, "remetente", "0001", "0012345", null, 1000);
        await Bradesco.OpenAccount(http, "destino", "1234", "98765", null, 0);
        return http;
    }

    private static async Task<string> Sent(HttpClient http, decimal valor) =>
        (await (await Efetiva(http, Codigo(), valor)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chaveUnicaParaApi").GetString()!;

    private static Task<HttpResponseMessage> Consulta(HttpClient http, string chaveUnica) =>
        http.GetAsync($"bradesco/transferencia/ted/v1/consulta?numeroDocumento={int.Parse(chaveUnica[..7])}&dataOperacao={Today}");

    // BradescoTedTransferRequestModel.ToJson(): numbers for agency/account/amount, fixed fields included.
    private static Task<HttpResponseMessage> Efetiva(HttpClient http, string codigo, decimal valor, long contaRemetente = 12345, string numeroFilial = "00024") =>
        http.PostAsJsonAsync("bradesco/transferencia/ted/v1/efetiva", new
        {
            identificadorDoTipoDeTransferencia = 1, agenciaRemetente = 1, contaRemetenteComDigito = contaRemetente, tipoContaRemetente = "CC",
            tipoDePessoaRemetente = "J", nomeClienteRemetente = "ACME", bancoDestinatario = 341, agenciaDestinatario = 1234,
            contaDestinatario = 987654L, tipoDeContaDestinatario = "CC", tipodePessoaDestinatario = "J", nomeClienteDestinatario = "ACME SUPPLIER",
            numeroInscricao = "123456789", numeroFilial, numeroControle = "81", valorDaTransferencia = valor, finalidadeDaTransferencia = 5,
            tipoDeDoc = "E", dataMovimento = Today, codigoIdentificadorDaTransferencia = codigo, canalPagamento = 0, valorMulta = 0m, valorJuro = 0m,
            valorDescontoOuAbatimento = 0m, valorOutrosAcrescimos = 0m, indicadorDda = "N", tipoDeDocumentoDeBarras = "", numeroCodigoDeBarras = "",
        });
}
