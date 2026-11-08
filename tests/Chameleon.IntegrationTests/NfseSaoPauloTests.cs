using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// Sao Paulo NFS-e behavior end to end.
public sealed class NfseSaoPauloTests
{
    private const string WebhookUrl = "https://app-under-test.local/webhooks/nfse";

    [Fact]
    public async Task Ttl_auto_authorizes_and_fires_webhook()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        var rps = NewRps();

        var emitted = await Emit(http, rps);
        Assert.Equal("Processando", emitted.GetProperty("status").GetString());

        await http.PostAsJsonAsync("/_chameleon/clock/advance", new { seconds = 400 });

        var nfse = await http.GetFromJsonAsync<JsonElement>($"/nfse-sp/nfse/{rps}");
        Assert.Equal("Autorizada", nfse.GetProperty("status").GetString());
        Assert.StartsWith("SP-", nfse.GetProperty("protocolo").GetString());

        await factory.SettledAsync();
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(WebhookUrl, url);
        Assert.Equal("nfse.autorizada", JsonDocument.Parse(body).RootElement.GetProperty("evento").GetString());
    }

    [Fact]
    public async Task Reject_fires_webhook_and_blocks_further_transitions()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        var rps = NewRps();
        await Emit(http, rps);

        var rejected = await http.PostAsJsonAsync($"/_chameleon/nfse-sp/nfse/{rps}/reject", new { reason = "invalid CNPJ" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await factory.SettledAsync();
        var (_, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal("invalid CNPJ", JsonDocument.Parse(body).RootElement.GetProperty("motivo").GetString());

        var authorize = await http.PostAsync($"/_chameleon/nfse-sp/nfse/{rps}/authorize", null);
        Assert.Equal(HttpStatusCode.Conflict, authorize.StatusCode);
    }

    [Fact]
    public async Task Same_rps_is_idempotent()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        var rps = NewRps();

        await Emit(http, rps);
        await Emit(http, rps);

        var verify = await http.PostAsJsonAsync("/_chameleon/verify",
            new { connector = "nfse-sp", entityId = rps, kind = "StateTransition" });
        var result = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("count").GetInt32());
    }

    private static string NewRps() => "RPS-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> Emit(HttpClient http, string rps)
    {
        var resp = await http.PostAsJsonAsync("/nfse-sp/nfse", new { valor = 250, webhookUrl = WebhookUrl, rps });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
