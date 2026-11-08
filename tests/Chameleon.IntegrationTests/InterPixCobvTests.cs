using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.InterTests;

namespace Chameleon.IntegrationTests;

/// Inter Pix with a due date (cobv) as a client uses it: loc, cobv, the sandbox payment with an amount,
/// the received Pix lookup and its refund.
public sealed class InterPixCobvTests
{
    private const string Chave = "inter-pix@example.com";
    private const string PixHook = "https://app-under-test.local/webhooks/inter/pix";
    // The scopes a client asks for.
    private const string ClientScopes = "cobv.read cobv.write payloadlocation.read payloadlocation.write pix.read pix.write pagamento-pix.write extrato.read boleto-cobranca.write boleto-cobranca.read";

    [Fact]
    public async Task Cobv_requires_the_cobv_scopes()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-inter-cobv-scope", "cob.write");
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{NewTxId()}", CobvBody(null))).AssertStatus(HttpStatusCode.Forbidden);
        (await http.PostAsJsonAsync("inter/pix/v2/loc", new { tipoCob = "cobv" })).AssertStatus(HttpStatusCode.Forbidden);

        await Token(http, issued, ClientScopes);
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{NewTxId()}", CobvBody(null))).AssertStatus(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cobv_is_created_on_a_loc_in_the_shape_a_client_reads()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-cobv", ClientScopes);

        var created = await http.PostAsJsonAsync("inter/pix/v2/loc", new { tipoCob = "cobv" });
        created.AssertStatus(HttpStatusCode.Created);
        var loc = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, loc.GetProperty("id").ValueKind);
        Assert.Equal("cobv", loc.GetProperty("tipoCob").GetString());
        (await http.PostAsJsonAsync("inter/pix/v2/loc", new { tipoCob = "outro" })).AssertStatus(HttpStatusCode.BadRequest);

        var txid = NewTxId();
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{txid}", CobvBody(loc))).AssertStatus(HttpStatusCode.Created);
        var cobv = await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/cobv/{txid}");
        Assert.Equal(loc.GetProperty("id").GetInt32(), cobv.GetProperty("loc").GetProperty("id").GetInt32());
        Assert.Equal("ATIVA", cobv.GetProperty("status").GetString());
        Assert.Equal("maria@example.com", cobv.GetProperty("devedor").GetProperty("email").GetString());
        Assert.Equal("50.00", cobv.GetProperty("valor").GetProperty("original").GetString());
        Assert.False(cobv.GetProperty("valor").TryGetProperty("desconto", out _)); // a client may type desconto off the Bacen schema
        Assert.Empty(cobv.GetProperty("pix").EnumerateArray());

        var unknown = JsonDocument.Parse("""{"id":999,"location":"x","tipoCob":"cobv","criacao":"2026-01-01T00:00:00Z"}""").RootElement;
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{NewTxId()}", CobvBody(unknown))).AssertStatus(HttpStatusCode.BadRequest);

        // Without loc the PSP creates one.
        var other = NewTxId();
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{other}", CobvBody(null))).AssertStatus(HttpStatusCode.Created);
        Assert.Equal(2, (await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/cobv/{other}")).GetProperty("loc").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Cobv_paid_with_another_amount_shows_it_in_the_lookup_and_the_webhook_and_can_be_refunded()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-cobv-pay", ClientScopes);
        // The client never registers its webhook: it is set by hand, as in the Inter portal.
        (await http.PutAsJsonAsync($"_chameleon/connectors/inter/webhook-registrations/{Chave}", new { url = PixHook })).EnsureSuccessStatusCode();
        var txid = NewTxId();
        (await http.PutAsJsonAsync($"inter/pix/v2/cobv/{txid}", CobvBody(null))).AssertStatus(HttpStatusCode.Created);

        var paid = await http.PostAsJsonAsync($"inter/pix/v2/cobv/pagar/{txid}", new { valor = 45.5m });
        paid.AssertStatus(HttpStatusCode.Created);
        var e2e = (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("e2e").GetString()!;
        (await http.PostAsJsonAsync($"inter/pix/v2/cobv/pagar/{txid}", new { valor = 45.5m })).AssertStatus(HttpStatusCode.BadRequest);

        var pix = await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/pix/{e2e}");
        Assert.Equal((txid, "45.50"), (pix.GetProperty("txid").GetString(), pix.GetProperty("valor").GetString()));
        Assert.True(pix.GetProperty("horario").TryGetDateTimeOffset(out _));
        (await http.GetAsync("inter/pix/v2/pix/E00000000NAOEXISTE")).AssertStatus(HttpStatusCode.NotFound);
        Assert.Equal("CONCLUIDA", (await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/cobv/{txid}")).GetProperty("status").GetString());

        var refund = await http.PutAsJsonAsync($"inter/pix/v2/pix/{e2e}/devolucao/{Guid.NewGuid():N}", new { valor = "4.50" });
        refund.AssertStatus(HttpStatusCode.Created);
        Assert.Equal("DEVOLVIDO", (await refund.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var refunded = await http.GetFromJsonAsync<JsonElement>($"inter/pix/v2/pix/{e2e}");
        Assert.Equal("4.50", refunded.GetProperty("devolucoes")[0].GetProperty("valor").GetString());

        await factory.SettledAsync("ci-inter-cobv-pay");
        var notified = factory.Webhooks.Received.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("pix")[0]).ToList();
        Assert.Equal(2, notified.Count);
        Assert.Equal("45.50", notified[0].GetProperty("valor").GetString());
        Assert.False(notified[0].TryGetProperty("devolucoes", out _));
        Assert.Equal("DEVOLVIDO", notified[1].GetProperty("devolucoes")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cobranca_valor_nominal_is_a_number()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-inter-valor");
        var codigo = await Emit(http, "NUM-1");
        var cobranca = (await http.GetFromJsonAsync<JsonElement>($"inter/cobranca/v3/cobrancas/{codigo}")).GetProperty("cobranca");
        Assert.Equal(JsonValueKind.Number, cobranca.GetProperty("valorNominal").ValueKind);
        Assert.Equal(100m, cobranca.GetProperty("valorNominal").GetDecimal());
    }

    private static string NewTxId() => $"{Guid.NewGuid():N}mpx";

    // A client body: the whole loc object, debtor address.
    private static object CobvBody(JsonElement? loc) => new
    {
        calendario = new { dataDeVencimento = Today().AddDays(1).ToString("yyyy-MM-dd") },
        devedor = new { nome = "Maria Souza", cep = "01000000", cidade = "Sao Paulo", email = "maria@example.com", uf = "SP", logradouro = "Rua A", cpf = "12345678909" },
        loc,
        valor = new { original = "50.00" },
        chave = Chave,
        solicitacaoPagador = "Pedido 123",
    };
}
