using System.IO.Compression;
using System.Net;
using Chameleon.CatalogVerify;
using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

public sealed class CatalogVerifierTests
{
    private static readonly string Providers = Path.Combine(TestPlugins.RepoRoot, "catalog", "providers");
    private static Catalog Catalog => Chameleon.Host.Plugins.Catalog.Parse(File.ReadAllText(TestPlugins.CatalogPath));

    [Fact]
    public void Clean_entries_have_no_findings()
    {
        foreach (var entry in Catalog.Entries)
            Assert.Empty(CatalogVerifier.CheckEntry(entry, TestPlugins.Feed, Providers));
    }

    [Fact]
    public void Wrong_hash_is_a_finding()
    {
        var entry = Catalog.Entries[0];
        var bad = entry with { Closure = [.. entry.Closure.Select((c, i) => i == 0 ? c with { Sha512 = new string('A', 88) } : c)] };
        Assert.Contains(CatalogVerifier.CheckEntry(bad, TestPlugins.Feed, Providers), f => f.Check == "hash");
    }

    [Fact]
    public void Closure_must_match_the_regenerated_one()
    {
        var entry = Catalog.Entries[0];
        var missing = entry with { Closure = [entry.Closure[0]] };
        if (entry.Closure.Count > 1) Assert.Contains(CatalogVerifier.CheckEntry(missing, TestPlugins.Feed, Providers), f => f.Check == "closure");
        var extra = entry with { Closure = [.. entry.Closure, new PackageRef("Some.Other", "1.0.0", "x")] };
        Assert.Contains(CatalogVerifier.CheckEntry(extra, TestPlugins.Feed, Providers), f => f.Check == "closure");
    }

    [Theory]
    [InlineData("Proprietary")]
    [InlineData("")]
    [InlineData("SSPL-1.0")]
    public void Non_osi_license_is_a_finding(string license) =>
        Assert.Contains(CatalogVerifier.CheckEntry(Catalog.Entries[0] with { License = license }, TestPlugins.Feed, Providers), f => f.Check == "license");

    [Theory]
    [InlineData("")]
    [InlineData("http://github.com/x/y")]
    [InlineData("git@github.com:x/y.git")]
    public void Repository_must_be_https(string repository) =>
        Assert.Contains(CatalogVerifier.CheckEntry(Catalog.Entries[0] with { Repository = repository }, TestPlugins.Feed, Providers), f => f.Check == "repository");

    [Fact] // the package's .nuspec is the source of truth: a catalog that only claims an accepted value is a finding
    public void Catalog_metadata_must_match_the_package()
    {
        var entry = Catalog.Entries[0];
        Assert.Equal("Apache-2.0", entry.License);
        Assert.Contains(CatalogVerifier.CheckEntry(entry with { License = "MIT" }, TestPlugins.Feed, Providers),
            f => f.Check == "metadata" && f.Message.Contains("license", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CatalogVerifier.CheckEntry(entry with { Repository = "https://example.com/other" }, TestPlugins.Feed, Providers),
            f => f.Check == "metadata" && f.Message.Contains("repository", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Provider_sheet_needs_https_sources()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var entry = Catalog.Entries[0];
            Assert.Contains(CatalogVerifier.CheckEntry(entry, TestPlugins.Feed, dir), f => f.Check == "provider-sheet");
            File.WriteAllText(Path.Combine(dir, $"{entry.Name}.md"), "# X\n\n## Sources\n\n- see the docs\n");
            Assert.Contains(CatalogVerifier.CheckEntry(entry, TestPlugins.Feed, dir), f => f.Check == "provider-sheet");
            File.WriteAllText(Path.Combine(dir, $"{entry.Name}.md"), "# X\n\n## Sources\n\n- https://example.com/docs\n");
            Assert.Contains(CatalogVerifier.CheckEntry(entry, TestPlugins.Feed, dir), f => f.Check == "provider-sheet" && f.Message.Contains("catalog says"));
            File.WriteAllText(Path.Combine(dir, $"{entry.Name}.md"), $"# X\n\n## Sources\n\n- {entry.Provider}\n");
            Assert.DoesNotContain(CatalogVerifier.CheckEntry(entry, TestPlugins.Feed, dir), f => f.Check == "provider-sheet");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] // D6: the OldSdk fixture, tagged as a connector, references a Chameleon.Sdk this Core does not accept
    public void Incompatible_sdk_is_a_finding()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var nupkg = Path.Combine(dir, "Fixture.Connector.OldSdk.0.1.0.nupkg");
            File.Copy(Path.Combine(TestPlugins.Feed, Path.GetFileName(nupkg)), nupkg);
            using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Update))
            {
                var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                string xml;
                using (var r = new StreamReader(nuspec.Open())) xml = r.ReadToEnd();
                xml = xml.Replace("</id>", "</id>\n    <tags>chameleon-connector chameleon-name:old-sdk</tags>", StringComparison.Ordinal);
                var name = nuspec.FullName;
                nuspec.Delete();
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml);
            }
            var entry = CatalogBuilder.Build(dir).Single();
            Assert.Contains(CatalogVerifier.CheckEntry(entry, dir, Providers),
                f => f.Check == "sdk" && f.Message.Contains("Fixture.Connector.OldSdk", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static Task<(int Exit, string Output)> RunVerifyAsync(string catalogJson) => RunVerifyAsync(catalogJson, TestPlugins.Feed);

    private static async Task<(int Exit, string Output)> RunVerifyAsync(string catalogJson, string sources)
    {
        var path = Path.Combine(Path.GetTempPath(), "chameleon-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, catalogJson);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
                $"\"{Path.Combine(AppContext.BaseDirectory, "catalog-verify.dll")}\" --catalog \"{path}\" --sources \"{sources}\" --providers \"{Providers}\" --skip-smoke --skip-network-checks")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync() + await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Empty_catalog_has_nothing_to_verify()
    {
        var (exit, output) = await RunVerifyAsync("[]\n");
        Assert.Equal(0, exit);
        Assert.Contains("nothing to verify", output);
    }

    [Fact]
    public async Task All_revoked_catalog_has_nothing_to_verify()
    {
        var revoked = Catalog.Entries.Take(1).Select(e => e with { Revoked = "compromised" });
        var (exit, output) = await RunVerifyAsync(Chameleon.Host.Plugins.Catalog.Serialize(revoked));
        Assert.Equal(0, exit);
        Assert.Contains("nothing to verify", output);
    }

    [Fact] // the workflows pass a feed URL: each entry is resolved from the source, not looked up as a local file
    public async Task Entries_are_resolved_from_a_feed_url()
    {
        var (exit, output) = await RunVerifyAsync(File.ReadAllText(TestPlugins.CatalogPath), new Uri(TestPlugins.Feed).AbsoluteUri);
        Assert.True(exit == 0, output);
        Assert.Equal(Catalog.Entries.Count, output.Split('\n').Count(l => l.TrimEnd().EndsWith(": ok", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Entry_missing_from_the_sources_fails()
    {
        var entries = Catalog.Entries.Select((e, i) => i == 0 ? e with { Version = "9.9.9" } : e).ToList();
        var (exit, output) = await RunVerifyAsync(Chameleon.Host.Plugins.Catalog.Serialize(entries), new Uri(TestPlugins.Feed).AbsoluteUri);
        Assert.NotEqual(0, exit);
        Assert.Contains($"{entries[0].Name} 9.9.9: FAIL", output);
        Assert.Contains("not found in the sources", output);
        Assert.Contains(output.Split('\n'), l => l.TrimEnd().EndsWith(": ok", StringComparison.Ordinal));
    }

    [Fact] // the real Host, one connector, offline, no outbound connection
    public async Task Contract_smoke_passes_for_a_catalog_connector()
    {
        var entry = Catalog.Entries.First(e => e.Name == "zendesk");
        var findings = await ContractSmoke.RunAsync(entry, TestPlugins.HostDll, TestPlugins.Feed, TestPlugins.CatalogPath, TimeSpan.FromSeconds(90), default);
        Assert.Empty(findings);
    }

    [Fact] // what the smoke relies on: a request through the proxy is counted
    public async Task Connection_counter_counts_a_proxied_request()
    {
        using var counter = new ConnectionCounter();
        using var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(counter.Url), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(2) };
        try { await http.GetAsync("http://example.com"); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        Assert.True(counter.Count >= 1);
    }
}
