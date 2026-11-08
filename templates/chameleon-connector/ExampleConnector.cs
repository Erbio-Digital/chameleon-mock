using System.Text.Json;
using Chameleon.Engine;
using Chameleon.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Example.Connector;

public enum ItemStatus { ACTIVE, ARCHIVED }

public sealed class Item : IHasNaturalKey
{
    public string Id { get; set; } = "";
    public string Key => Id;
    public string NaturalKeyProperty => nameof(Key);
    public string Name { get; set; } = "";
    public ItemStatus Status { get; set; } = ItemStatus.ACTIVE;
}

/// A minimal, complete connector: one entity with a lifecycle, two routes and one action.
/// See docs/creating-a-connector.md for simulated OAuth, webhooks and the sandbox clock.
public sealed class ExampleConnector : IConnector
{
    private static readonly Lifecycle<Item, ItemStatus> ItemLifecycle =
        new Lifecycle<Item, ItemStatus>("item", ItemStatus.ACTIVE, i => i.Key, i => i.Status, (i, s) => i.Status = s)
            .Allow(ItemStatus.ACTIVE, ItemStatus.ARCHIVED, "archive");

    public string Name => "example-connector-name";

    public ConnectorDescriptor Describe() => new(Name, [ItemLifecycle.Describe("id")],
    [
        ItemLifecycle.Action("archive", "Archive the item", (ctx, item, _) =>
        {
            ctx.Move(ItemLifecycle, item, ItemStatus.ARCHIVED, "action");
            return Task.FromResult(Results.Ok(item));
        }),
    ])
    {
        Title = "Example Title",
        Description = "Template connector: create and read items, archive them.",
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Simulated OAuth: add a token route and check credentials with ctx.CredentialMatches.
        routes.MapPost("/example-connector-name/items", async (HttpContext http, ConnectorContext ctx) =>
        {
            var body = await http.Request.ReadFromJsonAsync<JsonElement>();
            var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var n) ? n.GetString() : null;
            var item = new Item { Id = (ctx.Store.All<Item>().Count + 1).ToString(), Name = name ?? "" };
            ctx.Create(ItemLifecycle, item, "api");
            http.Annotate(entityId: item.Key);
            return Results.Json(item, statusCode: 201);
        });

        routes.MapGet("/example-connector-name/items/{id}", (string id, ConnectorContext ctx) =>
            ctx.Store.Get<Item>(id) is { } item ? Results.Json(item) : Results.NotFound(new { message = "item not found" }));
    }

    // Webhooks: call ctx.NotifyAsync(entityId, url, payload) after a state change.
    // Clock: override OnClockAdvancedAsync to age entities (TTL-driven transitions).
}
