using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chameleon.Host.Plugins;
using Microsoft.Extensions.Configuration;

namespace Chameleon.IntegrationTests.Plugins;

public sealed class MarketplaceTests
{
    [Fact] // the catalog entries, with what this instance loaded marked as installed
    public async Task Verified_lists_the_catalog_and_marks_installed()
    {
        using var factory = new ChameleonFactory();
        var list = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/_chameleon/marketplace/verified");
        var entries = list.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        foreach (var name in TestPlugins.Connectors.Split(','))
        {
            Assert.True(entries[name].GetProperty("installed").GetBoolean(), name);
            Assert.Equal(entries[name].GetProperty("version").GetString(), entries[name].GetProperty("installedVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entries[name].GetProperty("license").GetString()));
        }
    }

    [Fact] // no connector requested: the catalog is loaded on first use (D1)
    public async Task Verified_works_without_requested_connectors()
    {
        using var factory = new ChameleonFactory().WithWebHostBuilder(b => b.UseSetting("CHAMELEON_CONNECTORS", ""));
        var list = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/_chameleon/marketplace/verified");
        Assert.NotEmpty(list.EnumerateArray());
        Assert.All(list.EnumerateArray(), e => Assert.False(e.GetProperty("installed").GetBoolean()));
    }

    [Fact] // offline: no network, an explicit flag for the panel
    public async Task Community_is_empty_offline()
    {
        using var factory = new ChameleonFactory().WithWebHostBuilder(b => b.UseSetting("CHAMELEON_OFFLINE", "true"));
        var result = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/_chameleon/marketplace/community?q=bank");
        Assert.True(result.GetProperty("offline").GetBoolean());
        Assert.Empty(result.GetProperty("packages").EnumerateArray());
    }

    [Fact] // the test feed is a folder, not http: nothing to search, no error
    public async Task Community_skips_non_http_sources()
    {
        using var factory = new ChameleonFactory();
        var result = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/_chameleon/marketplace/community");
        Assert.False(result.GetProperty("offline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
    }

    [Fact] // unreachable http source: 200 with the error, never a 500
    public async Task Community_reports_search_errors()
    {
        using var factory = new ChameleonFactory().WithWebHostBuilder(b => b.UseSetting("CHAMELEON_NUGET_SOURCES", $"{TestPlugins.Feed},http://127.0.0.1:9/v3/index.json"));
        var response = await factory.CreateClient().GetAsync("/_chameleon/marketplace/community?q=x");
        response.AssertStatus(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(result.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Community_search_is_cached_per_term()
    {
        var calls = 0;
        var search = new CommunitySearch(PluginOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CHAMELEON_NUGET_SOURCES"] = "http://127.0.0.1:9/v3/index.json" }).Build()), (_, _) => { calls++; return Task.FromResult<IReadOnlyList<CommunityPackage>>([]); });
        await search.SearchAsync("a", default);
        await search.SearchAsync("a", default);
        await search.SearchAsync("b", default);
        Assert.Equal(2, calls);
    }

    [Fact] // the cache cannot grow without bound
    public async Task Community_cache_is_bounded()
    {
        var search = new CommunitySearch(PluginOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CHAMELEON_NUGET_SOURCES"] = "http://127.0.0.1:9/v3/index.json" }).Build()),
            (_, _) => Task.FromResult<IReadOnlyList<CommunityPackage>>([]));
        for (var i = 0; i < 250; i++) await search.SearchAsync($"term-{i}", default);
        Assert.InRange(search.CachedCount, 1, 200);
    }
}
