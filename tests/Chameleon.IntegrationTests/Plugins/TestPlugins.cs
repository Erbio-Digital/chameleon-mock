using System.Diagnostics;
using System.Reflection;
using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

/// Once per test run: packs the Engine and Sdk packages, the connector packages (and the OldSdk and Injected fixtures) into a local feed with a single
/// msbuild invocation, writes a catalog built from that feed and prepares the shared plugins directory every
/// ChameleonFactory loads its connectors from. Lives under the test output folder and is wiped at the next run.
public static class TestPlugins
{
    private static readonly string[] Projects =
    [
        "src/Chameleon.Engine/Chameleon.Engine.csproj",
        "src/Chameleon.Sdk/Chameleon.Sdk.csproj",
        "connectors/Chameleon.Connectors.Pix.Common/Chameleon.Connectors.Pix.Common.csproj",
        "connectors/Chameleon.Connectors.Bradesco/Chameleon.Connectors.Bradesco.csproj",
        "connectors/Chameleon.Connectors.Inter/Chameleon.Connectors.Inter.csproj",
        "connectors/Chameleon.Connectors.NfseSaoPaulo/Chameleon.Connectors.NfseSaoPaulo.csproj",
        "connectors/Chameleon.Connectors.Pix/Chameleon.Connectors.Pix.csproj",
        "connectors/Chameleon.Connectors.Zendesk/Chameleon.Connectors.Zendesk.csproj",
        "tests/fixtures/Fixture.Connector.OldSdk/Fixture.Connector.OldSdk.csproj",
        "tests/fixtures/Fixture.Connector.Injected/Fixture.Connector.Injected.csproj",
    ];

    public const string Connectors = "bradesco,inter,nfse-sp,pix,zendesk";

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "plugin-run");
    public static string Feed { get; } = Path.Combine(Root, "feed");
    public static string PluginsDir { get; } = Path.Combine(Root, "plugins");
    public static string CatalogPath { get; } = Path.Combine(Root, "catalog.json");
    public static string CatalogUrl { get; } = new Uri(CatalogPath).AbsoluteUri;
    public static string RepoRoot { get; } = FindRepoRoot();
    /// The Host build output next to the tests (its runtimeconfig.json is copied with it).
    public static string HostDll { get; } = Path.Combine(AppContext.BaseDirectory, "Chameleon.Host.dll");

    static TestPlugins()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        Directory.CreateDirectory(Feed);
        Directory.CreateDirectory(PluginsDir);
        Pack();
        File.WriteAllText(CatalogPath, Catalog.Serialize(CatalogBuilder.Build(Feed, Path.Combine(RepoRoot, "catalog", "providers"))));
    }

    /// A fresh plugins directory (resolvers that must not share the factory cache).
    public static string NewPluginsDir()
    {
        var dir = Path.Combine(Root, "plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Resolves a feed package into a fresh directory and loads its main assembly in its own context.
    public static async Task<Assembly> LoadAsync(string packageId, string assemblyFile)
    {
        var r = await new PackageResolver([Feed], NewPluginsDir(), offline: false).ResolveAsync(new(packageId, "0.1.0"), null, default);
        return new ConnectorLoadContext(packageId, r.Directory).LoadMain(assemblyFile);
    }

    private static void Pack()
    {
        var repo = RepoRoot;
        var items = string.Join(Environment.NewLine, Projects.Select(p => $"    <P Include=\"{Path.Combine(repo, p)}\" />"));
        var proj = Path.Combine(Root, "pack.proj");
        // Restore runs in its own evaluation (-restore); Pack then builds every project once, in parallel.
        File.WriteAllText(proj, $"""
            <Project>
              <ItemGroup>
            {items}
              </ItemGroup>
              <Target Name="Restore"><MSBuild Projects="@(P)" Targets="Restore" /></Target>
              <Target Name="PackAll">
                <MSBuild Projects="@(P)" Targets="Pack" BuildInParallel="true" Properties="Configuration=Release;PackageOutputPath={Feed}" />
              </Target>
            </Project>
            """);
        using var proc = Process.Start(new ProcessStartInfo("dotnet",
            $"msbuild \"{proj}\" -restore -t:PackAll -m -nodeReuse:false -nologo -v:q")
        { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"packing the test feed failed:\n{output}");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Chameleon.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Chameleon.sln not found above test base directory");
    }
}
