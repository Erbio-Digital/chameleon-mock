using Chameleon.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.Connector.B;

public sealed class FixtureBConnector : IConnector
{
    private static string JsonVersion => typeof(Newtonsoft.Json.JsonConvert).Assembly.GetName().Version!.ToString();

    public string Name => "fixture-b";

    public ConnectorDescriptor Describe() => new(Name, [], []) { Title = JsonVersion };

    public void MapRoutes(IEndpointRouteBuilder routes) =>
        routes.MapGet("/fixture-b/version", () => JsonVersion);
}

