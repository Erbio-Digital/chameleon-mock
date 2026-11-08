using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.Sdk;

public static class ConnectorDiscovery
{
    /// Registers every public concrete IConnector and runs every IConnectorModule found in the assembly.
    /// Returns the registered connector types.
    public static IReadOnlyList<Type> Register(Assembly assembly, IServiceCollection services)
    {
        var types = assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }).ToList();
        foreach (var module in types.Where(typeof(IConnectorModule).IsAssignableFrom))
            ((IConnectorModule)Activator.CreateInstance(module)!).ConfigureServices(services);
        var connectors = types.Where(typeof(IConnector).IsAssignableFrom).ToList();
        foreach (var connector in connectors)
            services.AddConnector(connector);
        return connectors;
    }
}
