using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Live events per sandbox over SignalR, only after commit.
[Collection(Serial.Name)]
public sealed class LiveEventsTests
{
    [Fact]
    public async Task Feed_receives_call_transition_and_webhook_only_for_its_sandbox()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        await using var qa = await LiveFeed.JoinAsync(factory, "qa");
        await using var hml = await LiveFeed.JoinAsync(factory, "hml");

        var http = await WebhookOutboxTests.SandboxClient(factory, "qa");
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null);

        await qa.WaitAsync(e => e.Type == "webhookUpdated" && e.Data.GetProperty("status").GetString() == "Delivered");
        Assert.Contains(qa.Events, e => e.Type == "interaction" && e.Data.GetProperty("summary").GetString() == $"PUT /bradesco/v2/cob/{txid}");
        Assert.Equal(["ATIVA", "CONCLUIDA"], qa.Events.Where(e => e.Type == "entityChanged" && e.Data.GetProperty("key").GetString() == txid)
            .Select(e => e.Data.GetProperty("state").GetString()));
        Assert.Contains(qa.Events, e => e.Type == "interaction" && e.Data.GetProperty("kind").GetString() == "OutboundWebhook");
        Assert.True(qa.Events.Where(e => e.Type == "interaction").All(e => e.Data.GetProperty("seq").GetInt64() > 0));

        Assert.DoesNotContain(hml.Events, e => e.Data.ToString().Contains(txid));
    }

    [Fact] // A refused transition publishes only the call
    public async Task Refused_transition_publishes_only_the_call()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/remove", null);
        await using var feed = await LiveFeed.JoinAsync(factory, Sandbox.Default);
        await Task.Delay(400); // the create/remove batch may still be on its way
        feed.Events.Clear();

        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);

        await feed.WaitAsync(e => e.Type == "interaction" && e.Data.GetProperty("statusCode").GetInt32() == 409);
        await Task.Delay(400); // one more batch window: nothing else may arrive
        Assert.DoesNotContain(feed.Events, e => e.Type is "entityChanged" or "webhookUpdated");
    }

    [Fact]
    public async Task Fault_arm_and_consume_are_published()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await using var feed = await LiveFeed.JoinAsync(factory, Sandbox.Default);

        (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Error", status = 503 })).EnsureSuccessStatusCode();
        await CreateCob(http, NewTxId());

        await feed.WaitAsync(e => e.Type == "faultChanged" && e.Data.GetProperty("remaining").GetInt32() == 0);
        Assert.Contains(feed.Events, e => e.Type == "faultChanged" && e.Data.GetProperty("remaining").GetInt32() == 1);
    }

    [Fact]
    public async Task Sandbox_changes_are_published()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        (await http.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ao-vivo" })).EnsureSuccessStatusCode();
        await using var feed = await LiveFeed.JoinAsync(factory, "ao-vivo");

        await http.PostAsJsonAsync("/s/ao-vivo/_chameleon/clock/advance", new { seconds = 60 });
        await feed.WaitAsync(e => e.Type == "sandboxChanged" && e.Data.TryGetProperty("now", out _));
        (await http.DeleteAsync("/_chameleon/sandboxes/ao-vivo")).EnsureSuccessStatusCode();
        await feed.WaitAsync(e => e.Type == "sandboxChanged" && e.Data.TryGetProperty("removed", out _));
    }

    [Fact]
    public async Task Burst_is_cut_with_overflow()
    {
        using var factory = new ChameleonFactory();
        await using var feed = await LiveFeed.JoinAsync(factory, Sandbox.Default);

        var burst = Enumerable.Range(0, 600).Select(i => new LiveEvent(LiveEvent.Interaction, new { n = i })).ToList();
        factory.Services.GetRequiredService<ILiveEventBus>().Publish(Sandbox.Default, burst);

        var overflow = await feed.WaitAsync(e => e.Type == "overflow");
        Assert.Equal(100, overflow.Data.GetProperty("dropped").GetInt32());
        Assert.Equal(500, feed.Events.Count(e => e.Type == "interaction" && e.Data.TryGetProperty("n", out _)));
    }

    [Fact]
    public async Task Joining_unknown_sandbox_fails()
    {
        using var factory = new ChameleonFactory();
        var ex = await Assert.ThrowsAsync<HubException>(() => LiveFeed.JoinAsync(factory, "does-not-exist"));
        Assert.Contains("sandbox not found", ex.Message);
    }
}

/// A panel's view of the hub: joins one sandbox and keeps every event it receives.
public sealed class LiveFeed : IAsyncDisposable
{
    public sealed record Event(string Type, JsonElement Data);

    private readonly HubConnection _connection;
    public ConcurrentQueue<Event> Events { get; } = new();

    private LiveFeed(HubConnection connection) => _connection = connection;

    public static async Task<LiveFeed> JoinAsync(ChameleonFactory factory, string sandbox)
    {
        factory.CreateClient(); // starts the server
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/_chameleon/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        var feed = new LiveFeed(connection);
        connection.On<JsonElement>("events", batch =>
        {
            foreach (var e in batch.GetProperty("events").EnumerateArray())
                feed.Events.Enqueue(new Event(e.GetProperty("type").GetString()!, e.GetProperty("data").Clone()));
        });
        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("Join", sandbox);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        return feed;
    }

    public async Task<Event> WaitAsync(Func<Event, bool> match, int timeoutMs = 10000)
    {
        Event? found = null;
        await ChameleonFactory.Eventually(() => (found = Events.FirstOrDefault(match)) is not null, timeoutMs);
        return found!;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
