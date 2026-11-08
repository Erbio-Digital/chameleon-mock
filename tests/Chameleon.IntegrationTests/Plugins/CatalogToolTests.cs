using Chameleon.CatalogTool;
using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

public sealed class CatalogToolTests
{
    [Fact]
    public async Task FromSources_MergesEntryByName()
    {
        var path = Path.Combine(Path.GetTempPath(), "chameleon-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        static string Stub(string name) => $$"""{"name":"{{name}}","packageId":"P.{{name}}","version":"0.0.1","title":"t","description":"d","provider":"p","repository":"r","license":"MIT","sdk":"0.0.x","closure":[{"id":"P.{{name}}","version":"0.0.1","sha512":"y"}],"revoked":null}""";
        // An outdated pix entry must be replaced, not duplicated.
        File.WriteAllText(path, $"[{Stub("zzz")},{Stub("pix")},{Stub("aaa")}]\n");
        try
        {
            var providers = Path.Combine(TestPlugins.RepoRoot, "catalog", "providers");
            Assert.Equal(0, await CatalogMerge.FromSourcesAsync([TestPlugins.Feed], ["Chameleon.Mock.Connectors.Pix@0.1.0"], providers, path, default));
            var entries = Catalog.Parse(File.ReadAllText(path)).Entries;
            Assert.Equal(["aaa", "pix", "zzz"], entries.Select(e => e.Name).ToArray());
            Assert.Equal(["Chameleon.Mock.Connectors.Pix", "Chameleon.Mock.Connectors.Pix.Common"], entries.Single(e => e.Name == "pix").Closure.Select(c => c.Id).Order().ToArray());
            Assert.Equal("https://bacen.github.io/pix-api/", entries.Single(e => e.Name == "pix").Provider);
        }
        finally { File.Delete(path); }
    }
}
