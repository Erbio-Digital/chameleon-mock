using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Internal destinations and hosts outside the allowlist are refused without connecting.
public sealed class WebhookDestinationPolicyTests
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data", "link-local")]
    [InlineData("http://127.0.0.1:9001/2018-06-01/runtime/invocation/next", "loopback")]
    [InlineData("http://10.0.0.5/hook", "private network")]
    [InlineData("http://[fe80::1]/hook", "link-local")]
    [InlineData("http://[::]:9001/2018-06-01/runtime/invocation/next", "non-routable")]
    [InlineData("http://0.0.0.0:9001/hook", "non-routable")]
    [InlineData("http://224.0.0.1/hook", "non-routable")]
    [InlineData("http://198.18.0.1/hook", "private network")]
    [InlineData("http://[64:ff9b::a00:5]/hook", "private network")]
    public async Task Internal_destination_is_blocked_without_connecting(string url, string reason)
    {
        var policy = new WebhookDestinationPolicy([]);
        var dispatcher = new HttpWebhookDispatcher(new HttpClient(policy.CreateHandler()), policy);

        var result = await dispatcher.SendAsync(url, "{}", TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(result.Blocked, result.Error);
        Assert.Contains(reason, result.Error);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Listed_host_may_be_private_but_never_link_local()
    {
        var policy = new WebhookDestinationPolicy(["localhost", ".interno.local"]);
        Assert.Null(policy.RefuseAddress("localhost", System.Net.IPAddress.Loopback));
        Assert.Null(policy.RefuseAddress("api.interno.local", System.Net.IPAddress.Parse("10.1.2.3")));
        Assert.NotNull(policy.RefuseAddress("api.interno.local", System.Net.IPAddress.Parse("169.254.169.254")));

        // Listed and loopback: it connects (and fails because nothing listens), it is not blocked.
        var dispatcher = new HttpWebhookDispatcher(new HttpClient(policy.CreateHandler()), policy);
        var result = await dispatcher.SendAsync("http://localhost:1/hook", "{}", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.False(result.Blocked);
    }

    [Fact]
    public async Task Destination_outside_allowlist_is_blocked_and_recorded()
    {
        using var factory = new ChameleonFactory(s => s.AddSingleton(new WebhookDestinationPolicy(["app-permitido.local"])));
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid); // webhookUrl on app-under-test.local, outside the allowlist
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();

        Assert.Empty(factory.Webhooks.Received);
        var delivery = (await http.GetFromJsonAsync<JsonElement>($"/_chameleon/webhooks?entityId={txid}"))[0];
        Assert.Equal("DeadLettered", delivery.GetProperty("status").GetString());
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());
        var attempt = (await http.GetFromJsonAsync<JsonElement>($"/_chameleon/webhooks/{delivery.GetProperty("id").GetString()}"))
            .GetProperty("attempts")[0];
        Assert.True(attempt.GetProperty("blocked").GetBoolean());
        Assert.Contains("outside the allowlist", attempt.GetProperty("error").GetString());
    }
}
