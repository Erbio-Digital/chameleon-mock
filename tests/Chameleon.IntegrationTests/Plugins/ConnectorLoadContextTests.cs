using System.Reflection;
using Chameleon.Host.Plugins;
using Chameleon.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.IntegrationTests.Plugins;

internal static class Fixtures
{
    public static string Dir(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static Assembly Load(string ctxName, string fixture) =>
        new ConnectorLoadContext(ctxName, Dir(fixture)).LoadMain($"Fixture.Connector.{fixture}.dll");
}

public class ConnectorLoadContextTests
{
    private static IConnector Instance(Assembly a) =>
        (IConnector)Activator.CreateInstance(a.GetExportedTypes().Single(typeof(IConnector).IsAssignableFrom))!;

    [Fact]
    public void TwoConnectors_WithConflictingDependency_LoadSideBySide()
    {
        var ca = Instance(Fixtures.Load("fixture-a", "A"));
        var cb = Instance(Fixtures.Load("fixture-b", "B"));
        Assert.StartsWith("12.", ca.Describe().Title);
        Assert.StartsWith("13.", cb.Describe().Title);
    }

    [Fact]
    public void SharedContract_IsTheHostType()
    {
        var a = Fixtures.Load("fixture-a2", "A");
        var type = a.GetExportedTypes().Single(t => t.GetInterfaces().Any(i => i.FullName == typeof(IConnector).FullName));
        Assert.True(typeof(IConnector).IsAssignableFrom(type));
    }

    [Fact]
    public void FixtureOutput_DoesNotShipSharedAssemblies()
    {
        foreach (var d in new[] { "A", "B" })
        {
            Assert.False(File.Exists(Path.Combine(Fixtures.Dir(d), "Chameleon.Sdk.dll")));
            Assert.False(File.Exists(Path.Combine(Fixtures.Dir(d), "Chameleon.Engine.dll")));
        }
    }

    [Fact]
    public void Discovery_RunsModuleOfLoadedAssembly()
    {
        var a = Fixtures.Load("fixture-a3", "A");
        var services = new ServiceCollection();
        var connectors = ConnectorDiscovery.Register(a, services);
        Assert.Single(connectors);
        Assert.Contains(services, s => s.ServiceType.Name == "FixtureAMarker");
    }

    [Theory]
    [InlineData("Chameleon.Sdk", true)]
    [InlineData("Chameleon.Engine", true)]
    [InlineData("Microsoft.Extensions.Http", true)]
    [InlineData("Newtonsoft.Json", false)]
    [InlineData("Chameleon.Connectors.Pix.Common", false)]
    public void IsShared(string name, bool expected) =>
        Assert.Equal(expected, SharedAssemblies.IsShared(new AssemblyName(name)));
}
