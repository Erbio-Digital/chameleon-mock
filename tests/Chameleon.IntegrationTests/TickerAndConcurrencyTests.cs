using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Sdk;
using Chameleon.Engine;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Realtime expiry on its own, and two changes racing on one entity.
[Collection(Serial.Name)]
public sealed class TickerAndConcurrencyTests
{
    [Fact]
    public async Task Realtime_sandbox_expires_on_its_own()
    {
        using var factory = new ChameleonFactory();
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = "tempo-real", clockMode = "Realtime" }))
            .EnsureSuccessStatusCode();
        var http = await WebhookOutboxTests.SandboxClient(factory, "tempo-real");
        var txid = NewTxId();
        (await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", new
        {
            calendario = new { expiracao = 1 },
            valor = new { original = "1.00" },
            chave = "chave",
        })).EnsureSuccessStatusCode();

        string? status = null;
        await ChameleonFactory.EventuallyAsync(async () =>
        {
            status = (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("status").GetString();
            return status == "REMOVIDA_PELO_PSP";
        }, timeoutMs: 6000);

        var verify = await http.PostAsJsonAsync("_chameleon/verify", new { entityId = txid, kind = "StateTransition" });
        var transition = (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[1];
        Assert.Equal("ttl", transition.GetProperty("detail").GetString());
    }

    [Fact] // At the store
    public void Stale_write_conflicts_in_memory()
    {
        using var factory = new ChameleonFactory();
        StaleWriteConflicts(factory.Services);
    }

    internal static void StaleWriteConflicts(IServiceProvider services)
    {
        var key = "race-" + Guid.NewGuid().ToString("N")[..8];
        using (var seed = Scope(services))
        {
            seed.Scope.Put(key, new Counter { Key = key, Value = 0 });
            seed.Scope.Commit();
        }

        using var first = Scope(services);
        using var second = Scope(services);
        var a = first.Scope.Get<Counter>(key)!;
        var b = second.Scope.Get<Counter>(key)!;
        a.Value = 1;
        first.Scope.Put(key, a);
        b.Value = 2;
        second.Scope.Put(key, b);

        first.Scope.Commit();
        Assert.Throws<ConcurrencyConflictException>(() => second.Scope.Commit());

        using var check = Scope(services);
        Assert.Equal(1, check.Scope.Get<Counter>(key)!.Value);
    }

    [Fact] // Through HTTP: the request that lost the race gets 409 and writes nothing
    public async Task Request_that_lost_the_race_gets_409()
    {
        using var factory = new ChameleonFactory(s => s.AddConnector<RaceConnector>());
        var http = factory.CreateClient();
        (await http.PutAsync("/race/r1", null)).EnsureSuccessStatusCode();

        RaceConnector.Release = new TaskCompletionSource();
        var slow = http.PostAsync("/race/r1/slow", null);
        await ChameleonFactory.Eventually(() => RaceConnector.Waiting);
        (await http.PostAsync("/race/r1/fast", null)).EnsureSuccessStatusCode();
        RaceConnector.Release.SetResult();

        var lost = await slow;
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.Equal("fast", (await http.GetFromJsonAsync<JsonElement>("/race/r1")).GetProperty("by").GetString());

        var verify = await http.PostAsJsonAsync("/_chameleon/verify", new { connector = "race", statusCode = 409 });
        var calls = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, calls.GetProperty("count").GetInt32());
    }

    internal sealed record ScopeHandle(IServiceScope Inner, RequestScope Scope) : IDisposable
    {
        public void Dispose() => Inner.Dispose();
    }

    internal static ScopeHandle Scope(IServiceProvider services)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SandboxContext>().Use(services.GetRequiredService<ISandboxStore>().Get(Sandbox.Default)!);
        return new ScopeHandle(scope, scope.ServiceProvider.GetRequiredService<RequestScope>());
    }

    public sealed class Counter : IHasNaturalKey
    {
        public required string Key { get; init; }
        public string NaturalKeyProperty => nameof(Key);
        public int Value { get; set; }
        public string By { get; set; } = "";
    }

    /// "slow" reads, waits until the test lets it go, then writes: by then "fast" already committed.
    private sealed class RaceConnector : IConnector
    {
        public static TaskCompletionSource Release = new();
        public static volatile bool Waiting;

        public string Name => "race";
        public ConnectorDescriptor Describe() => new(Name, [], []);

        public void MapRoutes(IEndpointRouteBuilder routes)
        {
            routes.MapPut("/race/{id}", (string id, ConnectorContext ctx) =>
            {
                ctx.Save(id, new Counter { Key = id });
                return Results.Ok();
            });
            routes.MapGet("/race/{id}", (string id, ConnectorContext ctx) =>
                Results.Ok(new { by = ctx.Store.Get<Counter>(id)?.By }));
            routes.MapPost("/race/{id}/slow", async (string id, ConnectorContext ctx) =>
            {
                var c = ctx.Store.Get<Counter>(id)!;
                Waiting = true;
                await Release.Task;
                c.By = "slow";
                ctx.Save(id, c);
                return Results.Ok();
            });
            routes.MapPost("/race/{id}/fast", (string id, ConnectorContext ctx) =>
            {
                var c = ctx.Store.Get<Counter>(id)!;
                c.By = "fast";
                ctx.Save(id, c);
                return Results.Ok();
            });
        }
    }
}
