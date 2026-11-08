using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using Chameleon.Host;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Retention over the in-memory stores (same use case as Postgres): wall-clock age, not sandbox time.
public sealed class RetentionTests
{
    [Fact]
    public async Task Keeps_recent_history_even_when_the_sandbox_clock_moved()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        await factory.SettledAsync();
        await http.PostAsJsonAsync("/_chameleon/clock/advance", new { seconds = 30 * 86400 });
        var outbox = factory.Services.GetRequiredService<IWebhookOutbox>();

        RetentionService.Purge(factory.Services, DateTimeOffset.UtcNow, RetentionService.DefaultRetentionDays);
        Assert.True((await Verify(http, new { entityId = txid })).GetProperty("count").GetInt32() > 0);
        Assert.NotEmpty(outbox.Find(Sandbox.Default, txid));

        RetentionService.Purge(factory.Services, DateTimeOffset.UtcNow.AddDays(8), RetentionService.DefaultRetentionDays);
        Assert.Equal(0, (await Verify(http, new { entityId = txid })).GetProperty("count").GetInt32());
        Assert.Empty(outbox.Find(Sandbox.Default, txid));
        // The simulated token of the call above went with them: the next call needs a new one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateCob(http, NewTxId())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/_chameleon/sandboxes/hml")).StatusCode);
    }
}
