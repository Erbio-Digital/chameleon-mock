using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chameleon.Sdk;

public static class ConnectorServiceExtensions
{
    /// Registers a connector. The host discovers every registered IConnector; nothing else to wire.
    public static IServiceCollection AddConnector<TConnector>(this IServiceCollection services)
        where TConnector : class, IConnector =>
        services.AddConnector(typeof(TConnector));

    public static IServiceCollection AddConnector(this IServiceCollection services, Type connectorType)
    {
        services.TryAddScoped<ConnectorContext>();
        services.AddSingleton(typeof(IConnector), connectorType);
        return services;
    }

    /// The optional JSON body of an action (its parameters), or null when empty.
    public static async Task<JsonElement?> ReadActionArgsAsync(this HttpRequest request)
    {
        if (request.ContentLength is 0 or null && !request.Headers.TransferEncoding.Any()) return null;
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        return doc.RootElement.Clone();
    }
}
