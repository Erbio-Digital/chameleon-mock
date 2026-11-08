using Microsoft.AspNetCore.Routing;

namespace Chameleon.Sdk;

/// The single surface a new provider simulator implements. A connector depends only on this
/// SDK, never on the host, the engine internals or another connector, so adding one never
/// requires editing the engine or the host routes.
public interface IConnector
{
    string Name { get; }

    /// Entities, lifecycles and actions: what the panel and the generic action route use.
    ConnectorDescriptor Describe();

    /// The provider surface (and any legacy trigger aliases). Capture and fault injection apply
    /// to every route mapped here without any code in the connector.
    void MapRoutes(IEndpointRouteBuilder routes);

    /// Called when the sandbox clock advances: age entities (TTL-driven transitions) here.
    Task OnClockAdvancedAsync(ConnectorContext ctx) => Task.CompletedTask;
}
