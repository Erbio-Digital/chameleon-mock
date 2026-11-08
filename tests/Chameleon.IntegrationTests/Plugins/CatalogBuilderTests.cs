using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

public sealed class CatalogBuilderTests
{
    [Fact]
    public void Build_WithProviders_UsesSheetUrl()
    {
        var entries = CatalogBuilder.Build(TestPlugins.Feed, Path.Combine(TestPlugins.RepoRoot, "catalog", "providers"));
        var sheet = ProviderSheet.ProviderUrl(Path.Combine(TestPlugins.RepoRoot, "catalog", "providers"), "bradesco");
        Assert.NotNull(sheet);
        Assert.Equal(sheet, entries.Single(e => e.Name == "bradesco").Provider);
        Assert.StartsWith("https://", entries.Single(e => e.Name == "pix").Provider);
    }

    [Fact]
    public void Build_RejectsAnInvalidConnectorName()
    {
        var dir = Directory.CreateTempSubdirectory("chameleon-badname-").FullName;
        try
        {
            var nupkg = Path.Combine(dir, "Chameleon.Mock.Connectors.Pix.0.1.0.nupkg");
            File.Copy(Path.Combine(TestPlugins.Feed, Path.GetFileName(nupkg)), nupkg);
            File.Copy(Path.Combine(TestPlugins.Feed, "Chameleon.Mock.Connectors.Pix.Common.0.1.0.nupkg"), Path.Combine(dir, "Chameleon.Mock.Connectors.Pix.Common.0.1.0.nupkg"));
            using (var zip = System.IO.Compression.ZipFile.Open(nupkg, System.IO.Compression.ZipArchiveMode.Update))
            {
                var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                string xml;
                using (var r = new StreamReader(nuspec.Open())) xml = r.ReadToEnd();
                var name = nuspec.FullName;
                nuspec.Delete();
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml.Replace("chameleon-name:pix", "chameleon-name:../bradesco", StringComparison.Ordinal));
            }
            var ex = Assert.Throws<InvalidOperationException>(() => CatalogBuilder.Build(dir));
            Assert.Contains("Chameleon.Mock.Connectors.Pix", ex.Message);
            Assert.Contains("../bradesco", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Build_WithoutProviders_FallsBackToTheProjectUrl()
    {
        var bradesco = CatalogBuilder.Build(TestPlugins.Feed).Single(e => e.Name == "bradesco");
        Assert.Equal(bradesco.Repository, bradesco.Provider);
    }
}
