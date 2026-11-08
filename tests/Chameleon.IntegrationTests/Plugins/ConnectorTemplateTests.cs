using System.Diagnostics;
using Chameleon.Host.Plugins;
using Chameleon.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.IntegrationTests.Plugins;

public class ConnectorTemplateTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Template_GeneratesConnectorThatLoads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "chameleon-template-" + Guid.NewGuid().ToString("N"));
        var hive = Path.Combine(tmp, "hive");
        var src = Path.Combine(tmp, "src");
        var feed = Path.Combine(tmp, "feed");
        Directory.CreateDirectory(feed);
        try
        {
            var template = Path.Combine(TestPlugins.RepoRoot, "templates", "chameleon-connector");
            Run(tmp, $"new install \"{template}\" --debug:custom-hive \"{hive}\"");
            Run(tmp, $"new chameleon-connector -n Acme.Chameleon.Echo --connectorName echo --title Echo -o \"{src}\" --debug:custom-hive \"{hive}\"");
            // Isolated global packages folder: a test-built Chameleon.Mock.Sdk 0.1.0 must never reach the developer's cache.
            File.WriteAllText(Path.Combine(src, "nuget.config"), $"""
                <configuration>
                  <config><add key="globalPackagesFolder" value="{Path.Combine(tmp, "packages")}" /></config>
                  <packageSources>
                    <clear />
                    <add key="test" value="{TestPlugins.Feed}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """);
            Run(src, $"pack -c Release -o \"{feed}\"");

            var resolved = await new PackageResolver([feed, TestPlugins.Feed], TestPlugins.NewPluginsDir(), offline: false)
                .ResolveAsync(new("Acme.Chameleon.Echo", "0.1.0"), null, default);
            var assembly = new ConnectorLoadContext("Acme.Chameleon.Echo", resolved.Directory).LoadMain("Acme.Chameleon.Echo.dll");
            var type = Assert.Single(ConnectorDiscovery.Register(assembly, new ServiceCollection()));
            var connector = (IConnector)Activator.CreateInstance(type)!;
            Assert.Equal("echo", connector.Name);
            Assert.Equal("Echo", connector.Describe().Title);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Run(string workDir, string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("dotnet", args)
        { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"dotnet {args} failed:\n{output}");
    }
}
