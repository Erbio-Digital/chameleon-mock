using Chameleon.Host.Plugins;
using Chameleon.Sdk;

namespace Chameleon.IntegrationTests.Plugins;

public class SdkCompatibilityTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.3", true)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("1.2.0", "1.9.0", true)]
    [InlineData("1.2.0", "2.0.0", false)]
    public void IsCompatible(string host, string connector, bool expected) =>
        Assert.Equal(expected, SdkCompatibility.IsCompatible(Version.Parse(host), Version.Parse(connector)));

    [Fact]
    public void ReferencedSdk_ReadsMetadata()
    {
        var path = Path.Combine(Fixtures.Dir("A"), "Fixture.Connector.A.dll");
        Assert.Equal(typeof(IConnector).Assembly.GetName().Version, SdkCompatibility.ReferencedSdk(path));
    }
}
