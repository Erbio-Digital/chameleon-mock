using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.Sdk;

/// Optional extra service registrations of a connector assembly (HttpClient, options...).
public interface IConnectorModule
{
    void ConfigureServices(IServiceCollection services);
}
