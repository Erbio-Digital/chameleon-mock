using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Inter: OAuth with scopes, Pix cob with the Inter extensions and Cobranca v3, in a subscribed
/// sandbox (mTLS both ways, https webhooks).
public sealed class InterTests
{
    private const string Chave = "inter-pix@example.com";
    private const string PixHook = "https://app-under-test.local/webhooks/inter/pix";
    private const string CobrancaHook = "https://app-under-test.local/webhooks/inter/boleto";
    private const string AllScopes = "cob.read cob.write pix.write webhook.read webhook.write boleto-cobranca.read boleto-cobranca.write";

    [Fact]
    public async Task Pix_cob_pays_with_inter_token_and_notifies_in_the_inter_shape()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-pix");
        (await http.PutAsJsonAsync($"inter/pix/v2/webhook/{Chave}", new { webhookUrl = "http://inseguro.local/hook" }))
            .AssertStatus(HttpStatusCode.BadRequest);
        (await http.PutAsJsonAsync($"inter/pix/v2/webhook/{Chave}", new { webhookUrl = PixHook })).AssertStatus(HttpStatusCode.NoContent);

        var created = await http.PostAsJsonAsync("inter/pix/v2/cob", new
        {
            calendario = new { expiracao = 3600 },
            devedor = new { cpf = "12345678909", nome = "Maria Souza" },
            valor = new { original = "50.00" },
            chave = Chave,
        });
        created.AssertStatus(HttpStatusCode.Created);
        var txid = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("txid").GetString()!;

        var paid = await http.PostAsync($"inter/pix/v2/cob/pagar/{txid}", null);
        paid.AssertStatus(HttpStatusCode.Created);
        Assert.StartsWith("E", (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("e2e").GetString());
        Assert.Equal("CONCLUIDA", (await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/cob/{txid}")).GetProperty("status").GetString());

        await factory.SettledAsync("ci-inter-pix");
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(PixHook, url);
        var pix = JsonDocument.Parse(body).RootElement.GetProperty("pix")[0];
        Assert.Equal(txid, pix.GetProperty("txid").GetString());
        Assert.Equal("Maria Souza", pix.GetProperty("pagador").GetProperty("nome").GetString());
        Assert.DoesNotContain("12345678909", body); // the Inter sends the document masked
        Assert.Equal("CN=inter.webhook.chameleon", Assert.Single(factory.Webhooks.Certificates));

        // A Bradesco credential is not an Inter credential.
        await TestAuth.Bradesco(http, "ci-inter-pix");
        await TestAuth.Subscribe(http, "inter"); // back to the Inter certificate, with the Bradesco bearer
        (await http.GetAsync($"inter/pix/v2/cob/{txid}")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Fact] // Scopes; the token request is form-urlencoded and its secret never lands in clear
    public async Task Token_grants_only_requested_scopes_and_secret_is_redacted()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-inter-scope", scopes: "cob.read");

        (await http.PostAsJsonAsync("inter/pix/v2/cob", new { valor = new { original = "1.00" }, chave = Chave }))
            .AssertStatus(HttpStatusCode.Forbidden);
        (await http.GetAsync("inter/cobranca/v3/cobrancas?dataInicial=2026-01-01&dataFinal=2026-12-31")).AssertStatus(HttpStatusCode.Forbidden);

        var noScope = await http.PostAsync("inter/oauth/v2/token", Form(issued, "escopo-inexistente"));
        noScope.AssertStatus(HttpStatusCode.BadRequest);

        var token = await Verify(http, new { connector = "inter", kind = "InboundRequest", summaryContains = "POST /inter/oauth/v2/token" });
        var request = token.GetProperty("matches")[0].GetProperty("requestBody").GetString()!;
        Assert.DoesNotContain(issued["CLIENT_SECRET"], request);
        Assert.Contains("client_secret=[redacted]", request);
    }

    [Fact]
    public async Task Boleto_is_issued_asynchronously_and_paid_via_pix()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-boleto");
        (await http.PutAsJsonAsync("inter/cobranca/v3/cobrancas/webhook", new { webhookUrl = CobrancaHook })).AssertStatus(HttpStatusCode.NoContent);

        var codigo = await Emit(http, "PED-1");
        // Same request again within 30 min: same charge.
        Assert.Equal(codigo, await Emit(http, "PED-1"));

        var processing = await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{codigo}");
        Assert.Equal("EM_PROCESSAMENTO", processing.GetProperty("cobranca").GetProperty("situacao").GetString());
        Assert.Equal(JsonValueKind.Null, processing.GetProperty("boleto").ValueKind);

        await Advance(http, 1);
        var issuedCob = await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{codigo}");
        Assert.Equal("A_RECEBER", issuedCob.GetProperty("cobranca").GetProperty("situacao").GetString());
        Assert.Equal(47, issuedCob.GetProperty("boleto").GetProperty("linhaDigitavel").GetString()!.Length);
        Assert.NotEmpty(issuedCob.GetProperty("pix").GetProperty("pixCopiaECola").GetString()!);

        (await http.PostAsJsonAsync($"_chameleon/connectors/inter/entities/cobranca/{codigo}/actions/pay", new { payWith = "PIX" }))
            .AssertStatus(HttpStatusCode.OK);
        var received = await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{codigo}");
        Assert.Equal(("RECEBIDO", "PIX"), (received.GetProperty("cobranca").GetProperty("situacao").GetString(),
            received.GetProperty("cobranca").GetProperty("origemRecebimento").GetString()));

        await factory.SettledAsync("ci-inter-boleto");
        var callbacks = factory.Webhooks.Received.Select(r => JsonDocument.Parse(r.Body).RootElement[0]).ToList();
        Assert.All(factory.Webhooks.Received, r => Assert.Equal(CobrancaHook, r.Url));
        Assert.Equal(["A_RECEBER", "RECEBIDO"], callbacks.Select(c => c.GetProperty("situacao").GetString()));
        Assert.Equal(("PIX", "100.00"), (callbacks[1].GetProperty("origemRecebimento").GetString(), callbacks[1].GetProperty("valorTotalRecebido").GetString()));
    }

    [Fact]
    public async Task Boleto_becomes_overdue_and_a_cancelled_one_cannot_be_paid()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-inter-vencido");
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).DateTime);

        var vencendo = await Emit(http, "VENC-1", today, numDiasAgenda: 2);
        var cancelando = await Emit(http, "CANC-1", today.AddDays(10));
        await Advance(http, 1);

        await Advance(http, 86400);
        await Token(http, issued); // the simulated token expired with the day
        Assert.Equal("ATRASADO", await Situacao(http, vencendo));

        (await http.PostAsJsonAsync($"inter/cobranca/v3/cobrancas/{cancelando}/cancelar", new { motivoCancelamento = "ACERTOS" }))
            .AssertStatus(HttpStatusCode.Accepted);
        Assert.Equal("CANCELADO", await Situacao(http, cancelando));
        (await http.PostAsJsonAsync($"inter/cobranca/v3/cobrancas/{cancelando}/pagar", new { pagarCom = "PIX" })).AssertStatus(HttpStatusCode.BadRequest);
        var entity = await http.GetFromJsonAsync<JsonElement>($"_chameleon/connectors/inter/entities/cobranca/{cancelando}");
        Assert.DoesNotContain("pay", entity.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        // numDiasAgenda after the due date it expires.
        await Advance(http, 2 * 86400);
        await Token(http, issued);
        Assert.Equal("EXPIRADO", await Situacao(http, vencendo));

        var sumario = await http.GetFromJsonAsync<JsonElement>(
            $"inter/cobranca/v3/cobrancas/sumario?dataInicial={today:yyyy-MM-dd}&dataFinal={today.AddDays(30):yyyy-MM-dd}");
        Assert.Equal(["CANCELADO", "EXPIRADO"], sumario.EnumerateArray().Select(s => s.GetProperty("situacao").GetString()).Order());
    }

    [Fact]
    public async Task Account_without_pix_key_is_credited_by_its_boleto_only()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-conta");
        (await http.PostAsJsonAsync("_chameleon/connectors/inter/accounts", new { nickname = "inter-op", branch = "0001", account = "1234567-8" }))
            .AssertStatus(HttpStatusCode.Created);

        http.DefaultRequestHeaders.Add("x-conta-corrente", "12345678");
        var codigo = await Emit(http, "CONTA-1");
        await Advance(http, 1);
        (await http.PostAsJsonAsync($"inter/cobranca/v3/cobrancas/{codigo}/pagar", new { pagarCom = "BOLETO" })).AssertStatus(HttpStatusCode.NoContent);

        // A Pix cob to a key nobody owns credits nothing.
        var cob = await http.PostAsJsonAsync("inter/pix/v2/cob", new { valor = new { original = "30.00" }, chave = "chave-sem-dono" });
        var txid = (await cob.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("txid").GetString();
        (await http.PostAsync($"inter/pix/v2/cob/pagar/{txid}", null)).AssertStatus(HttpStatusCode.Created);

        var account = await http.GetFromJsonAsync<JsonElement>("_chameleon/accounts/inter-op");
        Assert.Equal("100.00", account.GetProperty("balance").GetString());
        var credit = Assert.Single(account.GetProperty("entries").EnumerateArray());
        Assert.Equal(("Boleto recebido", codigo), (credit.GetProperty("description").GetString(), credit.GetProperty("reference").GetString()));

        http.DefaultRequestHeaders.Remove("x-conta-corrente");
        http.DefaultRequestHeaders.Add("x-conta-corrente", "999");
        (await http.PostAsJsonAsync("inter/cobranca/v3/cobrancas", EmitBody("CONTA-2", Today()))).AssertStatus(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----

    internal static async Task<(HttpClient Http, Dictionary<string, string> Issued)> Sandbox(ChameleonFactory factory, string id, string scopes = AllScopes)
    {
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id })).EnsureSuccessStatusCode();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{id}/");
        var issued = await TestAuth.Subscribe(http, "inter");
        await Token(http, issued, scopes);
        return (http, issued);
    }

    internal static async Task Token(HttpClient http, Dictionary<string, string> issued, string scopes = AllScopes)
    {
        var token = await http.PostAsync("inter/oauth/v2/token", Form(issued, scopes));
        token.AssertStatus(HttpStatusCode.OK);
        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(scopes, body.GetProperty("scope").GetString());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> issued, string scope) => new(new Dictionary<string, string>
    {
        ["client_id"] = issued["CLIENT_ID"], ["client_secret"] = issued["CLIENT_SECRET"], ["grant_type"] = "client_credentials", ["scope"] = scope,
    });

    internal static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).DateTime);

    internal static object EmitBody(string seuNumero, DateOnly vencimento, int numDiasAgenda = 0) => new
    {
        seuNumero,
        valorNominal = 100.00m,
        dataVencimento = vencimento.ToString("yyyy-MM-dd"),
        numDiasAgenda,
        pagador = new { cpfCnpj = "12345678909", tipoPessoa = "FISICA", nome = "Maria Souza", endereco = "Rua A", cidade = "Sao Paulo", uf = "SP", cep = "01000000" },
    };

    internal static async Task<string> Emit(HttpClient http, string seuNumero, DateOnly? vencimento = null, int numDiasAgenda = 0)
    {
        var resp = await http.PostAsJsonAsync("inter/cobranca/v3/cobrancas", EmitBody(seuNumero, vencimento ?? Today().AddDays(5), numDiasAgenda));
        resp.AssertStatus(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigoSolicitacao").GetString()!;
    }

    internal static async Task<string?> Situacao(HttpClient http, string codigo) =>
        (await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{codigo}")).GetProperty("cobranca").GetProperty("situacao").GetString();

    internal static async Task Advance(HttpClient http, int seconds) =>
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds })).EnsureSuccessStatusCode();
}

internal static class ResponseAssertions
{
    public static void AssertStatus(this HttpResponseMessage resp, HttpStatusCode expected)
    {
        if (resp.StatusCode != expected)
            Assert.Fail($"esperado {(int)expected}, veio {(int)resp.StatusCode}: {resp.Content.ReadAsStringAsync().Result}");
    }
}
