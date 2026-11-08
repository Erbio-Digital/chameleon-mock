using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Extra headers per destination host, for receivers that ask for a key (a request bin). An engine
/// setting of the sandbox, never a provider feature: the provider routes do not change.
public sealed class WebhookHeadersTests
{
    private const string Secret = "receiver-key-123";

    [Fact]
    public async Task Webhook_to_the_host_carries_the_header_and_the_journal_hides_it()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        (await SetHeaders(http, new { host = "app-under-test.local", name = "x-api-key", value = Secret })).AssertStatus(HttpStatusCode.OK);

        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();

        Assert.Equal(Secret, Assert.Single(factory.Webhooks.ReceivedHeaders)["x-api-key"]);
        var journal = await http.GetStringAsync($"/_chameleon/journal?entityId={txid}&kind=OutboundWebhook");
        Assert.DoesNotContain(Secret, journal);
        Assert.Contains("x-api-key", journal);
    }

    [Fact]
    public async Task Other_hosts_never_receive_it()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await SetHeaders(http, new { host = "outro-receptor.local", name = "x-api-key", value = Secret });

        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();

        Assert.False(Assert.Single(factory.Webhooks.ReceivedHeaders).ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task Value_is_never_shown_back_and_is_kept_when_omitted()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await SetHeaders(http, new { host = "app-under-test.local", name = "x-api-key", value = Secret });

        var kept = await SetHeaders(http,
            new { host = "app-under-test.local", name = "x-api-key", value = (string?)null },
            new { host = "app-under-test.local", name = "x-tenant", value = "qa" });
        var view = await kept.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, view.GetProperty("webhookHeaders").GetArrayLength());
        Assert.DoesNotContain(Secret, await http.GetStringAsync("/_chameleon/sandboxes"));

        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();
        var sent = Assert.Single(factory.Webhooks.ReceivedHeaders);
        Assert.Equal(Secret, sent["x-api-key"]);
        Assert.Equal("qa", sent["x-tenant"]);
    }

    [Theory]
    [InlineData("https://app.local", "x-api-key", "v")]
    [InlineData("app.local", "Content-Type", "v")]
    [InlineData("app.local", "x api key", "v")]
    [InlineData("app.local", "x-api-key", null)]
    public async Task Invalid_header_is_refused(string host, string name, string? value)
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        (await SetHeaders(http, new { host, name, value })).AssertStatus(HttpStatusCode.BadRequest);
    }

    private static Task<HttpResponseMessage> SetHeaders(HttpClient http, params object[] headers) =>
        http.PutAsJsonAsync("/_chameleon/sandboxes/default/webhook-headers", new { headers });
}
