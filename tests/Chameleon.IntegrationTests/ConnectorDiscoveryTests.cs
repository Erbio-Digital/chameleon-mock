using Chameleon.IntegrationTests.Plugins;
using Chameleon.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.IntegrationTests;

public class ConnectorDiscoveryTests
{
    [Fact]
    public async Task Register_FindsConnectorsAndRunsModules()
    {
        var assembly = await TestPlugins.LoadAsync("Chameleon.Mock.Connectors.Zendesk", "Chameleon.Connectors.Zendesk.dll");
        var services = new ServiceCollection();
        var connector = Assert.Single(ConnectorDiscovery.Register(assembly, services));
        Assert.Equal("ZendeskConnector", connector.Name);
        Assert.Contains(services, d => d.ServiceType == typeof(IConnector) && d.ImplementationType == connector);
    }

    [Fact]
    public void Register_AssemblyWithoutConnectors_ReturnsEmpty()
    {
        Assert.Empty(ConnectorDiscovery.Register(typeof(string).Assembly, new ServiceCollection()));
    }
}
