using Chameleon.Host.Plugins;
using Chameleon.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Chameleon.IntegrationTests.Plugins;

public class PluginLoaderTests(LocalFeed feed) : IClassFixture<LocalFeed>
{
    private const string Zendesk = "Chameleon.Mock.Connectors.Zendesk";
    private const string Injected = "Fixture.Plugin.Injected";

    private static Catalog Catalog() => new(CatalogBuilder.Build(TestPlugins.Feed));

    private PluginOptions Options(string[] requested, bool allowUnverified = false, string? source = null) =>
        new(requested, allowUnverified, Offline: false, feed.NewPluginsDir(), CatalogUrl: "file:///unused", [source ?? feed.Path]);

    private static Task<IReadOnlyList<LoadedPackage>> Load(PluginOptions options, Catalog catalog, IServiceCollection? services = null) =>
        PluginLoader.LoadAsync(options, catalog, services ?? new ServiceCollection(), NullLogger.Instance, default);

    /// Loads, builds the provider and returns what Verify reports (names from the connectors DI built).
    private static async Task<(IReadOnlyList<LoadedConnector> Connectors, ServiceProvider Provider)> LoadAndBuild(PluginOptions options, Catalog catalog)
    {
        var services = new ServiceCollection();
        await Load(options, catalog, services);
        var sp = services.BuildServiceProvider();
        return (PluginLoader.Verify(sp), sp);
    }

    /// A one-package feed: {id} 0.1.0 whose lib/net8.0/{id}.dll is the given file.
    private static string FeedWith(string id, string dll)
    {
        var dir = TestPlugins.NewPluginsDir();
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse("0.1.0"), Description = "test" };
        builder.Authors.Add("test");
        builder.Files.Add(new PhysicalPackageFile { SourcePath = dll, TargetPath = $"lib/net8.0/{id}.dll" });
        using var fs = File.Create(Path.Combine(dir, $"{id}.0.1.0.nupkg"));
        builder.Save(fs);
        return dir;
    }

    [Fact]
    public async Task Load_ShortNames_RegistersConnectorsAsVerified()
    {
        var services = new ServiceCollection();
        var packages = await Load(Options(["bradesco", "zendesk"]), Catalog(), services);
        Assert.Equal(["Chameleon.Mock.Connectors.Bradesco", Zendesk], packages.Select(p => p.PackageId).ToArray());
        Assert.All(packages, p => Assert.True(p.Verified));
        using var sp = services.BuildServiceProvider();
        Assert.Same(packages, sp.GetRequiredService<IReadOnlyList<LoadedPackage>>());
        var connectors = PluginLoader.Verify(sp);
        Assert.Equal(["bradesco", "zendesk"], connectors.Select(c => c.Name).Order().ToArray());
        Assert.Contains(new LoadedConnector("zendesk", Zendesk, "0.1.0", true), connectors);
    }

    [Fact]
    public async Task Load_ConstructorInjection_AndMainDllFallback()
    {
        // Package id Fixture.Plugin.Injected, assembly Fixture.Connector.Injected.dll: the single main dll is used.
        var (connectors, sp) = await LoadAndBuild(Options([$"{Injected}@0.1.0"], allowUnverified: true), Catalog());
        using (sp)
        {
            Assert.Equal(new LoadedConnector("zendesk", Injected, "0.1.0", false), Assert.Single(connectors));
            Assert.Equal("injected", sp.GetRequiredService<IConnector>().Describe().Title);
        }
    }

    [Fact]
    public async Task Load_UnknownName_Throws()
    {
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() => Load(Options(["nope"]), Catalog()));
        Assert.Contains("unknown connector 'nope'", ex.Message);
    }

    [Fact]
    public async Task Load_Unverified_WithoutFlag_Throws()
    {
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() => Load(Options([$"{Zendesk}@0.1.0"]), Catalog()));
        Assert.Contains("CHAMELEON_ALLOW_UNVERIFIED", ex.Message);
    }

    [Fact]
    public async Task Load_Unverified_WithFlag_LoadsAsUnverified()
    {
        var (connectors, sp) = await LoadAndBuild(Options([$"{Zendesk}@0.1.0"], allowUnverified: true), Catalog());
        using (sp) Assert.Equal(new LoadedConnector("zendesk", Zendesk, "0.1.0", false), Assert.Single(connectors));
    }

    [Fact]
    public async Task Load_Revoked_Throws()
    {
        var catalog = new Catalog(Catalog().Entries.Select(e => e.Name == "zendesk" ? e with { Revoked = "signing key leaked" } : e).ToList());
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() => Load(Options(["zendesk"]), catalog));
        Assert.Contains("revoked", ex.Message);
        Assert.Contains("signing key leaked", ex.Message);
    }

    [Fact]
    public async Task Load_DuplicateName_Throws()
    {
        // Two distinct packages whose connectors are both named "zendesk": caught once DI built them.
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            LoadAndBuild(Options(["zendesk", $"{Injected}@0.1.0"], allowUnverified: true), Catalog()));
        Assert.Contains("duplicate connector name 'zendesk'", ex.Message);
        Assert.Contains(Zendesk, ex.Message);
        Assert.Contains(Injected, ex.Message);
    }

    [Fact]
    public void Verify_ConnectorConstructorThrows_NamesPackage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<LoadedPackage>>([new LoadedPackage(typeof(object).Assembly, "Some.Broken.Pkg", "0.1.0", false)]);
        services.AddSingleton<IConnector>(_ => throw new InvalidOperationException("boom"));
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<PluginLoadException>(() => PluginLoader.Verify(sp));
        Assert.Contains("Some.Broken.Pkg", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Load_IncompatibleSdk_Throws()
    {
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            Load(Options(["Fixture.Connector.OldSdk@0.1.0"], allowUnverified: true), Catalog()));
        Assert.Contains("Fixture.Connector.OldSdk", ex.Message);
        Assert.Contains("Chameleon.Sdk 0.2.0", ex.Message);
        Assert.Contains("needs 0.1.x", ex.Message);
    }

    [Fact]
    public async Task Load_NoConnectorInPackage_Throws()
    {
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            Load(Options(["Chameleon.Mock.Connectors.Pix.Common@0.1.0"], allowUnverified: true), Catalog()));
        Assert.Contains("Chameleon.Mock.Connectors.Pix.Common", ex.Message);
        Assert.Contains("no IConnector", ex.Message);
    }

    [Fact]
    public async Task Load_DllWithoutSdkReference_Throws()
    {
        var source = FeedWith("Fixture.NoSdk", typeof(FactAttribute).Assembly.Location);
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            Load(Options(["Fixture.NoSdk@0.1.0"], allowUnverified: true, source), Catalog()));
        Assert.Contains("Fixture.NoSdk", ex.Message);
        Assert.Contains("does not reference Chameleon.Sdk", ex.Message);
    }

    [Fact]
    public async Task Load_GarbageDll_Throws()
    {
        var garbage = Path.Combine(TestPlugins.NewPluginsDir(), "garbage.dll");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        var source = FeedWith("Fixture.Garbage", garbage);
        var ex = await Assert.ThrowsAsync<PluginLoadException>(() =>
            Load(Options(["Fixture.Garbage@0.1.0"], allowUnverified: true, source), Catalog()));
        Assert.Contains("Fixture.Garbage", ex.Message);
        Assert.Contains("cannot load", ex.Message);
    }

    [Fact]
    public async Task Load_Empty_StartsWithoutConnectors()
    {
        var services = new ServiceCollection();
        Assert.Empty(await Load(Options([]), Catalog(), services));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IConnector));
        Assert.Contains(services, s => s.ServiceType == typeof(IReadOnlyList<LoadedPackage>));
    }

    [Fact]
    public void Options_FromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CHAMELEON_CONNECTORS"] = " zendesk, Some.Package@1.0.0 ,",
            ["CHAMELEON_ALLOW_UNVERIFIED"] = "true",
            ["CHAMELEON_NUGET_SOURCES"] = "a, b",
        }).Build();
        var o = PluginOptions.From(config);
        Assert.Equal(["zendesk", "Some.Package@1.0.0"], o.Requested);
        Assert.True(o.AllowUnverified);
        Assert.False(o.Offline);
        Assert.Equal(["a", "b"], o.NugetSources);
        Assert.Equal("https://raw.githubusercontent.com/Erbio-Digital/chameleon-mock/main/catalog/connectors.json", o.CatalogUrl);
    }
}
