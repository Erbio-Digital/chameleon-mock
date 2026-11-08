using Chameleon.Host.Cli;
using Chameleon.IntegrationTests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Chameleon.IntegrationTests.Cli;

public class ChameleonCliTests(LocalFeed feed) : IClassFixture<LocalFeed>
{
    private (IConfiguration Config, string PluginsDir) Config(bool offline = false)
    {
        var dir = feed.NewPluginsDir();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CHAMELEON_NUGET_SOURCES"] = feed.Path,
            ["CHAMELEON_CATALOG_URL"] = TestPlugins.CatalogUrl,
            ["CHAMELEON_PLUGINS_DIR"] = dir,
            ["CHAMELEON_OFFLINE"] = offline ? "true" : "false",
        }).Build();
        return (config, dir);
    }

    private static async Task<(int? Code, string Output)> Run(IConfiguration config, params string[] args)
    {
        var output = new StringWriter();
        var code = await ChameleonCli.TryRunAsync(args, config, output, default);
        return (code, output.ToString());
    }

    [Fact]
    public async Task NotACommand_ReturnsNull() =>
        Assert.Null((await Run(Config().Config)).Code);

    [Fact]
    public async Task Install_Verified_WritesPluginsAndReports()
    {
        var (config, dir) = Config();
        var (code, output) = await Run(config, "install", "zendesk");
        Assert.Equal(0, code);
        Assert.Contains("installed zendesk 0.1.0 (verified)", output);
        Assert.NotEmpty(Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public async Task Install_Unknown_ExitsOne()
    {
        var (code, output) = await Run(Config().Config, "install", "nope");
        Assert.Equal(1, code);
        Assert.Contains("unknown connector", output);
    }

    [Fact]
    public async Task List_ShowsCatalog()
    {
        var (code, output) = await Run(Config().Config, "list");
        Assert.Equal(0, code);
        foreach (var name in new[] { "bradesco", "inter", "nfse-sp", "zendesk" }) Assert.Contains(name, output);
    }

    [Fact]
    public async Task List_MarksInstalled()
    {
        var (config, _) = Config();
        await Run(config, "install", "zendesk");
        var (_, output) = await Run(config, "list");
        Assert.Contains("[installed]", output.Split('\n').Single(l => l.Contains("zendesk")));
        Assert.DoesNotContain("[installed]", output.Split('\n').Single(l => l.Contains("bradesco")));
    }

    [Fact]
    public async Task Search_FiltersCatalogOffline()
    {
        var (code, output) = await Run(Config(offline: true).Config, "search", "pix");
        Assert.Equal(0, code);
        Assert.Contains("bradesco", output);
        Assert.Contains("inter", output);
        Assert.DoesNotContain("zendesk", output);
    }
}
