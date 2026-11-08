using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.InterTests;

namespace Chameleon.IntegrationTests;

/// Inter Banking: balance and statement from the sandbox ledger, outgoing Pix and
/// boleto payment settled after the deadline, operator settle/refuse, Hold on settlement.
public sealed class InterBankingTests
{
    private const string Scopes = "cob.read cob.write pix.write boleto-cobranca.read boleto-cobranca.write extrato.read " +
                                  "pagamento-pix.write pagamento-pix.read pagamento-boleto.write pagamento-boleto.read webhook-banking.write webhook-banking.read";
    private const string PixOutHook = "https://app-under-test.local/webhooks/inter/pix-pagamento";
    private const string BoletoOutHook = "https://app-under-test.local/webhooks/inter/boleto-pagamento";

    [Fact]
    public async Task Balance_statement_outgoing_pix_and_boleto_follow_the_ledger()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking", ("principal", "1234567-8", 1000m));

        var boleto = await Emit(http, "REC-200", null);
        await Advance(http, 1);
        (await http.PostAsJsonAsync($"_chameleon/connectors/inter/entities/cobranca/{boleto}/actions/pay", new { payWith = "PIX" })).AssertStatus(HttpStatusCode.OK);
        Assert.Equal(1100m, await Saldo(http)); // boleto issued at 100.00 by EmitBody
        var credit = (await Extrato(http)).Last();
        Assert.Equal(("C", "100.00", "PIX"), // boleto with Pix paid by Pix
            (Str(credit, "tipoOperacao"), Str(credit, "valor"), Str(credit, "tipoTransacao")));
        Assert.Equal(Today().ToString("yyyy-MM-dd"), Str(credit, "dataEntrada"));

        http.DefaultRequestHeaders.Add("x-id-idempotente", "repasse-0001");
        var pix = await PixOut(http, 300m, new { tipo = "CHAVE", chave = "external-key@example.com" });
        Assert.Equal(("APROVACAO", JsonValueKind.Null), (Str(pix, "tipoRetorno"), pix.GetProperty("endToEndId").ValueKind));
        var codigo = Str(pix, "codigoSolicitacao")!;
        // The Inter refuses a repeated idempotency key (the caller then looks in the statement).
        (await http.PostAsJsonAsync("inter/banking/v2/pix", new { valor = 300m, descricao = "Referencia: R1", destinatario = new { tipo = "CHAVE", chave = "x" } }))
            .AssertStatus(HttpStatusCode.UnprocessableEntity);
        http.DefaultRequestHeaders.Remove("x-id-idempotente");
        await Advance(http, 5); // the deadline, no action from anyone
        Assert.Equal("EFETIVADO", await PixStatus(http, codigo));
        Assert.Equal(800m, await Saldo(http));
        // Reconciliation key: the payment description comes back intact in the full statement.
        var completo = (await http.GetFromJsonAsync<JsonElement>($"inter/banking/v2/extrato/completo?{Period()}")).GetProperty("transacoes").EnumerateArray().Last();
        Assert.Equal(("Referencia: R1", codigo), (Str(completo.GetProperty("detalhes"), "descricaoPix"), Str(completo.GetProperty("detalhes"), "codigoSolicitacao")));

        var pay = await http.PostAsJsonAsync("inter/banking/v2/pagamento", Boleto(new string('3', 47), 150m));
        pay.AssertStatus(HttpStatusCode.OK);
        var payment = await pay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APROVADO", Str(payment, "statusPagamento"));
        await Advance(http, 5);
        Assert.Equal(650m, await Saldo(http));
        var debit = (await Extrato(http)).Last();
        Assert.Equal(("D", "150.00", "PAGAMENTO"), (Str(debit, "tipoOperacao"), Str(debit, "valor"), Str(debit, "tipoTransacao")));
        var listed = Assert.Single((await http.GetFromJsonAsync<JsonElement>($"inter/banking/v2/pagamento?{Period()}")).EnumerateArray());
        Assert.Equal("REALIZADO", Str(listed, "statusPagamento"));

        await factory.SettledAsync("ci-banking");
        var pixCallback = Callbacks(factory, PixOutHook).Single();
        Assert.Equal(("EFETIVADO", codigo, "PAGAMENTO"), (Str(pixCallback, "status"), Str(pixCallback, "codigoSolicitacao"), Str(pixCallback, "tipoMovimentacao")));
        Assert.StartsWith("E", Str(pixCallback, "endToEnd"));
        Assert.Equal("REALIZADO", Str(Callbacks(factory, BoletoOutHook).Single(), "status"));
    }

    [Fact]
    public async Task Outgoing_above_balance_or_from_unknown_account_is_refused()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking-balance", ("principal", "1234567-8", 100m));

        (await http.PostAsJsonAsync("inter/banking/v2/pix", new { valor = 300m, destinatario = new { tipo = "CHAVE", chave = "x" } }))
            .AssertStatus(HttpStatusCode.UnprocessableEntity);
        (await http.PostAsJsonAsync("inter/banking/v2/pagamento", Boleto(new string('4', 44), 300m))).AssertStatus(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(100m, await Saldo(http));

        http.DefaultRequestHeaders.Add("x-conta-corrente", "999999");
        (await http.GetAsync("inter/banking/v2/saldo")).AssertStatus(HttpStatusCode.Forbidden);
        (await http.PostAsJsonAsync("inter/banking/v2/pix", new { valor = 1m, destinatario = new { tipo = "CHAVE", chave = "x" } }))
            .AssertStatus(HttpStatusCode.Forbidden);
        Assert.Single((await http.GetFromJsonAsync<JsonElement>("_chameleon/accounts/principal")).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Operator_settles_early_or_refuses_without_debit()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking-op", ("principal", "1234567-8", 1000m));

        var early = Str(await PixOut(http, 100m, new { tipo = "CHAVE", chave = "externa" }), "codigoSolicitacao")!;
        (await http.PostAsync($"_chameleon/connectors/inter/entities/pagamento/{early}/actions/settle", null)).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("EFETIVADO", await PixStatus(http, early));
        await Advance(http, 10); // the deadline passing later changes nothing
        Assert.Equal(900m, await Saldo(http));

        var refused = Str(await PixOut(http, 300m, new { tipo = "CHAVE", chave = "externa" }), "codigoSolicitacao")!;
        (await http.PostAsJsonAsync($"_chameleon/connectors/inter/entities/pagamento/{refused}/actions/reject",
            new { reason = "destination key not found" })).AssertStatus(HttpStatusCode.OK);
        var detail = await http.GetFromJsonAsync<JsonElement>($"inter/banking/v2/pix/{refused}");
        Assert.Equal("REJEITADO", detail.GetProperty("transacaoPix").GetProperty("status").GetString());
        Assert.Equal(900m, await Saldo(http));

        await factory.SettledAsync("ci-banking-op");
        var callbacks = Callbacks(factory, PixOutHook).ToList();
        Assert.Equal(["EFETIVADO", "REJEITADO"], callbacks.Select(c => Str(c, "status")));
        Assert.Equal("destination key not found", Str(callbacks[1], "motivo"));
    }

    [Fact]
    public async Task Held_settlement_waits_until_disarmed()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking-hold", ("principal", "1234567-8", 1000m));
        // The panel offers the settlement route from the description.
        var connectors = (await http.GetFromJsonAsync<JsonElement>("/_chameleon/connectors")).EnumerateArray().ToDictionary(c => Str(c, "name")!);
        Assert.Equal((true, false), (connectors["inter"].GetProperty("settlement").GetBoolean(), connectors["bradesco"].GetProperty("settlement").GetBoolean()));
        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "bradesco", route = "settlement", kind = "Hold" })).AssertStatus(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "inter", route = "settlement", kind = "Error" })).AssertStatus(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "inter", route = "settlement", kind = "Hold" })).AssertStatus(HttpStatusCode.OK);

        var codigo = Str(await PixOut(http, 50m, new { tipo = "CHAVE", chave = "externa" }), "codigoSolicitacao")!;
        await Advance(http, 30);
        Assert.Equal("APROVACAO", await PixStatus(http, codigo));
        Assert.Empty(factory.Webhooks.Received);

        (await http.DeleteAsync("_chameleon/faults?connector=inter&route=settlement")).AssertStatus(HttpStatusCode.NoContent);
        await Advance(http, 1);
        Assert.Equal("EFETIVADO", await PixStatus(http, codigo));
        await factory.SettledAsync("ci-banking-hold");
        Assert.Single(Callbacks(factory, PixOutHook));
    }

    [Fact]
    public async Task Each_outgoing_debits_the_chosen_account_and_credits_destinations_in_the_sandbox()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking-contas", ("conta-1", "111", 1000m), ("conta-2", "222", 50m));
        (await http.PostAsJsonAsync("_chameleon/connectors/bradesco/accounts",
            new { nickname = "recebimentos", branch = "0237", account = "999", pixKey = "recebe@bradesco", balance = 10m })).AssertStatus(HttpStatusCode.Created);

        // Without the header and with two Inter accounts, the bank asks which one.
        (await http.GetAsync("inter/banking/v2/saldo")).AssertStatus(HttpStatusCode.BadRequest);
        http.DefaultRequestHeaders.Add("x-conta-corrente", "111");

        var pix = Str(await PixOut(http, 200m, new { tipo = "CHAVE", chave = "recebe@bradesco" }), "codigoSolicitacao")!;
        (await http.PostAsync($"_chameleon/connectors/inter/entities/pagamento/{pix}/actions/settle", null)).AssertStatus(HttpStatusCode.OK);

        var boleto = await Emit(http, "PROPRIO-1", null); // issued by conta-1 itself
        await Advance(http, 1);
        var linha = (await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{boleto}")).GetProperty("boleto").GetProperty("linhaDigitavel").GetString()!;
        var pay = await (await http.PostAsJsonAsync("inter/banking/v2/pagamento", Boleto(linha, 100m))).Content.ReadFromJsonAsync<JsonElement>();
        (await http.PostAsync($"_chameleon/connectors/inter/entities/pagamento/{Str(pay, "codigoTransacao")}/actions/settle", null))
            .AssertStatus(HttpStatusCode.OK);
        Assert.Equal("RECEBIDO", await Situacao(http, boleto));

        var balances = (await http.GetFromJsonAsync<JsonElement>("_chameleon/accounts")).EnumerateArray()
            .ToDictionary(a => Str(a, "nickname")!, a => Str(a, "balance"));
        // conta-1: 1000 - 200 (Pix) - 100 (boleto paid) + 100 (its own boleto received).
        Assert.Equal(("800.00", "50.00", "210.00"), (balances["conta-1"], balances["conta-2"], balances["recebimentos"]));
    }

    [Fact] // PIX_RESPONSE = PROCESSADO: effective at once, with the end-to-end id in the response
    public async Task Processed_return_settles_in_the_request()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Banking(factory, "ci-banking-processado", ("principal", "1234567-8", 500m));
        (await http.PutAsJsonAsync("_chameleon/connectors/inter/subscription/settings", new Dictionary<string, string> { ["PIX_RESPONSE"] = "PROCESSADO" }))
            .EnsureSuccessStatusCode();

        var pix = await PixOut(http, 120m, new { tipo = "CHAVE", chave = "externa" });
        Assert.Equal("PROCESSADO", Str(pix, "tipoRetorno"));
        Assert.StartsWith("E", Str(pix, "endToEndId"));
        Assert.Equal(380m, await Saldo(http));
    }

    // ---- helpers ----

    private static async Task<(HttpClient Http, Dictionary<string, string> Issued)> Banking(ChameleonFactory factory, string id,
        params (string Apelido, string Conta, decimal Saldo)[] accounts)
    {
        var (http, issued) = await Sandbox(factory, id, Scopes);
        foreach (var (nickname, account, balance) in accounts)
            (await http.PostAsJsonAsync("_chameleon/connectors/inter/accounts", new { nickname, branch = "0001", account, balance })).AssertStatus(HttpStatusCode.Created);
        if (accounts.Length == 1)
        {
            (await http.PutAsJsonAsync("inter/banking/v2/webhooks/pix-pagamento", new { webhookUrl = PixOutHook })).AssertStatus(HttpStatusCode.NoContent);
            (await http.PutAsJsonAsync("inter/banking/v2/webhooks/boleto-pagamento", new { webhookUrl = BoletoOutHook })).AssertStatus(HttpStatusCode.NoContent);
        }
        return (http, issued);
    }

    private static async Task<JsonElement> PixOut(HttpClient http, decimal valor, object destinatario)
    {
        var resp = await http.PostAsJsonAsync("inter/banking/v2/pix", new { valor, descricao = "Referencia: R1", destinatario });
        resp.AssertStatus(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Boleto(string code, decimal valor) =>
        new { codBarraLinhaDigitavel = code, valorPagar = valor, dataVencimento = Today().AddDays(3).ToString("yyyy-MM-dd") };

    private static async Task<string?> PixStatus(HttpClient http, string codigo) =>
        (await http.GetFromJsonAsync<JsonElement>($"inter/banking/v2/pix/{codigo}")).GetProperty("transacaoPix").GetProperty("status").GetString();

    private static async Task<decimal> Saldo(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>("inter/banking/v2/saldo")).GetProperty("disponivel").GetDecimal();

    private static async Task<List<JsonElement>> Extrato(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>($"inter/banking/v2/extrato?{Period()}")).GetProperty("transacoes").EnumerateArray().ToList();

    private static string Period() => $"dataInicio={Today().AddDays(-1):yyyy-MM-dd}&dataFim={Today().AddDays(30):yyyy-MM-dd}";

    private static IEnumerable<JsonElement> Callbacks(ChameleonFactory factory, string url) =>
        factory.Webhooks.Received.Where(r => r.Url == url).Select(r => JsonDocument.Parse(r.Body).RootElement[0]);

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();
}
