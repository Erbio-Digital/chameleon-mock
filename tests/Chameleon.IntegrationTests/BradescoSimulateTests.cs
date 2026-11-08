using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// POST simulate/pix-payment-by-emv: not a bank API, what a client application calls in a test
/// environment to pay a charge; same effect as the "pay" action.
public sealed class BradescoSimulateTests
{
    [Fact] // valor as a number, pagador with cpf, infoPagador = reference
    public async Task Pays_the_charge_of_the_emv()
    {
        var (factory, http, sandbox) = await Bradesco.Sandbox("ci-sim");
        var txid = BradescoCobTests.ClientTxId();
        var cob = await (await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", BradescoCobTests.ClientBody("20.00"))).Content.ReadFromJsonAsync<JsonElement>();

        var paid = await http.PostAsJsonAsync("bradesco/simulate/pix-payment-by-emv", new
        {
            emv = cob.GetProperty("pixCopiaECola").GetString(), valor = 15.5m,
            pagador = new { cpf = "12345678909", nome = "Pagador simulado" }, infoPagador = "ref-1",
        });

        paid.AssertStatus(HttpStatusCode.OK);
        var pix = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("pix")[0];
        Assert.Equal("15.50", pix.GetProperty("valor").GetString());
        Assert.Equal("12345678909", pix.GetProperty("pagador").GetProperty("cpf").GetString());
        Assert.Equal("ref-1", pix.GetProperty("infoPagador").GetString());
        await factory.SettledAsync(sandbox);
        Assert.Single(factory.Webhooks.Received);
    }

    [Fact]
    public async Task Unknown_emv_is_not_found_and_paid_charge_is_refused()
    {
        var (_, http, _) = await Bradesco.Sandbox("ci-sim");
        var txid = BradescoPixTests.NewTxId();
        var emv = (await (await BradescoPixTests.CreateCob(http, txid)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pixCopiaECola").GetString();

        (await Simulate(http, "00020126-desconhecido")).AssertStatus(HttpStatusCode.NotFound);
        (await Simulate(http, emv!)).AssertStatus(HttpStatusCode.OK);
        (await Simulate(http, emv!)).AssertStatus(HttpStatusCode.BadRequest);
    }

    // Fixed payer, valor as a number.
    private static Task<HttpResponseMessage> Simulate(HttpClient http, string emv) =>
        http.PostAsJsonAsync("bradesco/simulate/pix-payment-by-emv", new
        {
            emv, valor = 123.45m, pagador = new { cpf = "11111111111", nome = "Jose da silva" }, infoPagador = "ref",
        });
}
