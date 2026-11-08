using Chameleon.Sdk;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.Connector.Injected;

public sealed class Greeting
{
    public string Text => "injected";
}

public sealed class InjectedConnector(Greeting greeting) : IConnector
{
    public string Name => "zendesk";

    public ConnectorDescriptor Describe() => new(Name, [], []) { Title = greeting.Text };

    public void MapRoutes(IEndpointRouteBuilder routes) { }
}

public sealed class InjectedModule : IConnectorModule
{
    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<Greeting>();
}
