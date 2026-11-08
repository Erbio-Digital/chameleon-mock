using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

public class PackageResolverTests : IClassFixture<LocalFeed>
{
    private readonly LocalFeed feed;
    public PackageResolverTests(LocalFeed feed) => this.feed = feed;

    [Fact]
    public async Task Resolve_DownloadsClosureAndFlattensLib()
    {
        var dir = feed.NewPluginsDir();
        var r = await new PackageResolver([feed.Path], dir, offline: false)
            .ResolveAsync(new("Chameleon.Mock.Connectors.Bradesco", "0.1.0"), null, default);
        Assert.Contains(r.Closure, p => p.Id == "Chameleon.Mock.Connectors.Pix.Common");
        Assert.DoesNotContain(r.Closure, p => p.Id == "Chameleon.Mock.Sdk");
        Assert.All(r.Closure, p => Assert.False(string.IsNullOrEmpty(p.Sha512)));
        Assert.True(File.Exists(Path.Combine(r.Directory, "Chameleon.Connectors.Bradesco.dll")));
        Assert.True(File.Exists(Path.Combine(r.Directory, "Chameleon.Connectors.Pix.Common.dll")));
        Assert.False(File.Exists(Path.Combine(r.Directory, "Chameleon.Sdk.dll")));
    }

    [Fact]
    public async Task Resolve_WrongHash_Throws()
    {
        var resolver = new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false);
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            resolver.ResolveAsync(new("Chameleon.Mock.Connectors.Zendesk", "0.1.0", Sha512: "AAAA"), null, default));
        Assert.Contains("Chameleon.Mock.Connectors.Zendesk", ex.Message);
        Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_TamperedCache_RedownloadsOnline_FailsClearOffline()
    {
        var dir = feed.NewPluginsDir();
        var main = new PackageRef("Chameleon.Mock.Connectors.Zendesk", "0.1.0");
        var good = (await new PackageResolver([feed.Path], dir, offline: false).ResolveAsync(main, null, default)).Main.Sha512!;
        var cached = Path.Combine(dir, "packages", "Chameleon.Mock.Connectors.Zendesk.0.1.0.nupkg");
        File.WriteAllText(cached, "tampered");
        var pins = new Dictionary<string, string> { [main.Id] = good };
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([], dir, offline: true).ResolveAsync(main, pins, default));
        Assert.Contains("clear the plugins cache", ex.Message);
        var r = await new PackageResolver([feed.Path], dir, offline: false).ResolveAsync(main, pins, default);
        Assert.Equal(good, r.Main.Sha512);
    }

    [Fact]
    public async Task Resolve_Offline_UsesCacheOnly()
    {
        var dir = feed.NewPluginsDir();
        await new PackageResolver([feed.Path], dir, offline: false).ResolveAsync(new("Chameleon.Mock.Connectors.Zendesk", "0.1.0"), null, default);
        var cached = await new PackageResolver([], dir, offline: true).ResolveAsync(new("Chameleon.Mock.Connectors.Zendesk", "0.1.0"), null, default);
        Assert.NotNull(cached);
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([], feed.NewPluginsDir(), offline: true).ResolveAsync(new("Chameleon.Mock.Connectors.Inter", "0.1.0"), null, default));
        Assert.Contains("CHAMELEON_OFFLINE", ex.Message);
    }

    [Fact]
    public async Task Resolve_PinnedClosureMismatch_Throws()
    {
        var ok = await new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
            .ResolveAsync(new("Chameleon.Mock.Connectors.Bradesco", "0.1.0"), null, default);
        var pinned = new Dictionary<string, string>
        {
            [ok.Main.Id] = ok.Main.Sha512!,
            ["Chameleon.Mock.Connectors.Pix.Common"] = "AAAA",
        };
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
                .ResolveAsync(new("Chameleon.Mock.Connectors.Bradesco", "0.1.0"), pinned, default));
        Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_PinnedClosure_MissingPin_Throws()
    {
        var dir = feed.NewPluginsDir();
        var ok = await new PackageResolver([feed.Path], dir, offline: false)
            .ResolveAsync(new("Chameleon.Mock.Connectors.Bradesco", "0.1.0"), null, default);
        var pinned = new Dictionary<string, string> { [ok.Main.Id] = ok.Main.Sha512! };
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
                .ResolveAsync(new("Chameleon.Mock.Connectors.Bradesco", "0.1.0"), pinned, default));
        Assert.Contains("Pix.Common", ex.Message);
    }

    [Fact]
    public async Task Resolve_PinnedClosure_ExtraPin_Throws()
    {
        var ok = await new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
            .ResolveAsync(new("Chameleon.Mock.Connectors.Zendesk", "0.1.0"), null, default);
        var pinned = new Dictionary<string, string> { [ok.Main.Id] = ok.Main.Sha512!, ["Some.Extra.Package"] = "AAAA" };
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
                .ResolveAsync(new("Chameleon.Mock.Connectors.Zendesk", "0.1.0"), pinned, default));
        Assert.Contains("Some.Extra.Package", ex.Message);
    }

    [Fact]
    public async Task Resolve_UnknownPackage_Throws()
    {
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            new PackageResolver([feed.Path], feed.NewPluginsDir(), offline: false)
                .ResolveAsync(new("Chameleon.Mock.Connectors.Nope", "0.1.0"), null, default));
        Assert.Contains("Chameleon.Mock.Connectors.Nope", ex.Message);
    }
}
