using Chameleon.Sdk;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;

namespace Chameleon.IntegrationTests;

/// What a request writes is committed together, or not at all.
public sealed class RequestScopeTests
{
    [Fact]
    public async Task Exception_discards_entities_and_journal()
    {
        using var factory = new ChameleonFactory(s => s.AddSingleton<IConnector, ExplodingConnector>());
        var http = factory.CreateClient();

        // Development host: the developer exception page turns the throw into a 500.
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, (await http.PostAsync("/exploding/thing/t-1", null)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/exploding/thing/t-1")).StatusCode);
        var transitions = await http.GetFromJsonAsync<JsonElement>("/_chameleon/journal?entityId=t-1&kind=StateTransition");
        Assert.Equal(0, transitions.GetArrayLength());

        // The failed call itself stays visible, with the error, for debugging.
        var calls = await http.GetFromJsonAsync<JsonElement>("/_chameleon/journal?entityId=t-1&kind=InboundRequest");
        var failed = calls.EnumerateArray().First(c => c.GetProperty("method").GetString() == "POST");
        Assert.Equal(500, failed.GetProperty("statusCode").GetInt32());
        Assert.Equal("unhandled exception: InvalidOperationException", failed.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Committed_request_is_visible_to_the_next_one()
    {
        using var factory = new ChameleonFactory(s => s.AddSingleton<IConnector, ExplodingConnector>());
        var http = factory.CreateClient();

        (await http.PutAsync("/exploding/thing/t-2", null)).EnsureSuccessStatusCode();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.GetAsync("/exploding/thing/t-2")).StatusCode);
    }

    private sealed class Thing : IHasNaturalKey
    {
        public required string Key { get; init; }
        public string NaturalKeyProperty => nameof(Key);
    }

    /// Writes an entity and a transition, then fails: nothing of it may survive.
    private sealed class ExplodingConnector : IConnector
    {
        public string Name => "exploding";

        public ConnectorDescriptor Describe() => new(Name, [], []);

        public void MapRoutes(IEndpointRouteBuilder routes)
        {
            routes.MapPost("/exploding/thing/{key}", (string key, RequestScope store, RequestScope journal, SandboxClock clock) =>
            {
                Write(key, store, journal, clock);
                throw new InvalidOperationException("boom after write");
            });
            routes.MapPut("/exploding/thing/{key}", (string key, RequestScope store, RequestScope journal, SandboxClock clock) =>
            {
                Write(key, store, journal, clock);
                return Results.Ok();
            });
            routes.MapGet("/exploding/thing/{key}", (string key, RequestScope store) =>
                store.Get<Thing>(key) is null ? Results.NotFound() : Results.Ok());
        }

        private static void Write(string key, RequestScope store, RequestScope journal, SandboxClock clock)
        {
            store.Put(key, new Thing { Key = key });
            journal.Record(new Interaction(InteractionKind.StateTransition, "exploding", key, clock.Now, "none -> created"));
        }
    }
}
