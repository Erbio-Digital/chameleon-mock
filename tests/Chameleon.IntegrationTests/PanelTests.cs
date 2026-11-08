using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chameleon.IntegrationTests;

/// The Host serves the panel build at /ui, outside the sandboxes.
public sealed class PanelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("chameleon-panel-").FullName;

    public PanelTests()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<div id=\"root\"></div>");
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "app-abc123.js"), "console.log(1)");
    }

    [Fact]
    public async Task Serves_assets_and_falls_back_to_index_only_under_ui()
    {
        using var factory = new ChameleonFactory();
        var http = factory.WithWebHostBuilder(b => b.UseSetting("PANEL_ROOT", _root))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var root = await http.GetAsync("/");
        Assert.Equal("/ui/", root.Headers.Location?.ToString());

        var deep = await http.GetAsync("/ui/s/qa/entities/bradesco/cob/abc");
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.Contains("id=\"root\"", await deep.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", deep.Headers.CacheControl?.ToString());

        var asset = await http.GetAsync("/ui/assets/app-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString());

        // Outside /ui nothing changed: an unknown provider path is still 404, and the panel is not journaled.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/bradesco/v2/does-not-exist")).StatusCode);
        var journal = await http.GetStringAsync("/_chameleon/journal");
        Assert.DoesNotContain("/ui", journal);
    }

    [Fact]
    public async Task Without_a_build_ui_says_how_to_build()
    {
        using var factory = new ChameleonFactory();
        var http = factory.WithWebHostBuilder(b => b.UseSetting("PANEL_ROOT", Path.Combine(_root, "does-not-exist"))).CreateClient();

        var response = await http.GetAsync("/ui/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("pnpm build", await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
