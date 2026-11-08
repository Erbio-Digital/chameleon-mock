using System.Text.Json;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Host.Http;

/// Entities of a connector in the current sandbox, as the panel shows them: state, masked data
///, and for one entity its timeline, deliveries and the actions allowed now.
public static class EntityEndpoints
{
    private const string Base = $"{ChameleonEndpoints.ControlPrefix}/connectors/{{connector}}/entities/{{type}}";

    public static void MapEntities(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Base, (string connector, string type, IEnumerable<IConnector> connectors, IEntityStore entities, SandboxContext sandbox) =>
        {
            var (_, entity) = Describe(connectors, connector, type);
            return entity?.ClrType is null
                ? NotFound(connector, type)
                : Results.Ok(entities.All(sandbox.Id, entity.ClrType).OrderBy(e => e.Key).Select(e => View(entity, e)));
        });

        routes.MapGet($"{Base}/{{key}}", (string connector, string type, string key, IEnumerable<IConnector> connectors,
            IEntityStore entities, IInteractionStore journal, IWebhookOutbox outbox, SandboxContext sandbox) =>
        {
            var (descriptor, entity) = Describe(connectors, connector, type);
            if (entity?.ClrType is null) return NotFound(connector, type);
            if (entities.Get(sandbox.Id, entity.ClrType, key) is not { } stored)
                return Results.NotFound(new { message = $"{type} {key} not found" });

            var state = entity.StateOf?.Invoke(stored.Entity);
            return Results.Ok(new
            {
                stored.Key,
                state,
                stored.Version,
                dueAt = EntityJson.DueAt(stored.Entity),
                stored.UpdatedAt,
                data = Masked(stored.Entity, entity.ClrType),
                allowedActions = descriptor!.Actions
                    .Where(a => a.EntityType == type && state is not null && a.AllowedFrom.Contains(state))
                    .Select(a => a.Name),
                timeline = journal.Find(new InteractionQuery(sandbox.Id, Connector: connector, EntityId: key)),
                webhooks = outbox.Find(sandbox.Id, key).Where(d => d.Connector == connector).Select(LiveEvents.Webhook),
            });
        });
    }

    private static (ConnectorDescriptor?, EntityDescriptor?) Describe(IEnumerable<IConnector> connectors, string connector, string type)
    {
        var descriptor = connectors.FirstOrDefault(c => c.Name == connector)?.Describe();
        return (descriptor, descriptor?.Entities.FirstOrDefault(e => e.Type == type));
    }

    private static object View(EntityDescriptor entity, StoredEntity e) => new
    {
        e.Key,
        state = entity.StateOf?.Invoke(e.Entity),
        e.Version,
        dueAt = EntityJson.DueAt(e.Entity),
        e.UpdatedAt,
        data = Masked(e.Entity, entity.ClrType!),
    };

    /// Same masking as the journal: CPF/CNPJ, names, e-mails and phones never reach the panel in clear.
    private static object? Masked(object entity, Type type)
    {
        var (body, _) = PayloadSanitizer.Body(EntityJson.Serialize(entity, type));
        try
        {
            return body is null ? null : JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            return body; // truncated above 64 KB: shown as text
        }
    }

    private static IResult NotFound(string connector, string type) =>
        Results.NotFound(new { message = $"unknown entity {type} in {connector}" });
}
