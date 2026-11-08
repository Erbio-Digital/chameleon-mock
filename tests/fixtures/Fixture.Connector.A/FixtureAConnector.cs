using Chameleon.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.Connector.A;

public sealed class FixtureAConnector : IConnector
{
    private static string JsonVersion => typeof(Newtonsoft.Json.JsonConvert).Assembly.GetName().Version!.ToString();

    public string Name => "fixture-a";

    public ConnectorDescriptor Describe() => new(Name, [], []) { Title = JsonVersion };

    public void MapRoutes(IEndpointRouteBuilder routes) =>
        routes.MapGet("/fixture-a/version", () => JsonVersion);
}

public sealed class FixtureAMarker;

public sealed class FixtureAModule : IConnectorModule
{
    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<FixtureAMarker>();
}

