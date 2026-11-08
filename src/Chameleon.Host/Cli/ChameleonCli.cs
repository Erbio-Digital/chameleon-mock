using Chameleon.Host.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chameleon.Host.Cli;

/// `chameleon install|list|search`: manages the plugins directory outside the running server.
public static class ChameleonCli
{
    private const string Usage = "usage: chameleon install <name|Id@version>... | list | search <term>";

    /// Returns null when args are not a CLI command (the web host starts); otherwise runs it and returns the exit code.
    public static async Task<int?> TryRunAsync(string[] args, IConfiguration config, TextWriter output, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("install" or "list" or "search")) return null;
        var options = PluginOptions.From(config);
        try
        {
            using var http = new HttpClient();
            var catalog = await Catalog.LoadAsync(options.CatalogUrl, options.Offline, http, NullLogger.Instance, ct);
            switch (args[0])
            {
                case "install" when args.Length > 1:
                    foreach (var requested in args.Skip(1))
                    {
                        var (_, version, verified) = await PluginLoader.InstallAsync(requested, options, catalog, NullLogger.Instance, ct);
                        output.WriteLine($"installed {requested.Split('@')[0]} {version} ({(verified ? "verified" : "unverified")})");
                    }
                    return 0;
                case "list":
                    List(options, catalog, output);
                    return 0;
                case "search" when args.Length > 1:
                    await SearchAsync(string.Join(' ', args.Skip(1)), options, catalog, output, ct);
                    return 0;
                default:
                    output.WriteLine(Usage);
                    return 2;
            }
        }
        catch (PluginLoadException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void List(PluginOptions options, Catalog catalog, TextWriter output)
    {
        var installed = Installed(options.PluginsDir);
        foreach (var e in catalog.Entries)
            output.WriteLine($"{e.Name} {e.Version} {e.Title}{(e.Revoked is null ? "" : " revoked")}"
                + (installed.Contains(e.PackageId) ? " [installed]" : ""));
    }

    private static async Task SearchAsync(string term, PluginOptions options, Catalog catalog, TextWriter output, CancellationToken ct)
    {
        var found = catalog.Entries.Where(e => Has(e.Name, term) || Has(e.Title, term) || Has(e.Description, term)).ToList();
        foreach (var e in found) output.WriteLine($"{e.Name} {e.Version} {e.Title}{(e.Revoked is null ? "" : " revoked")}");
        if (options.Offline) return;
        try
        {
            var hits = await new CommunitySearch(options).SearchAsync(term, ct);
            foreach (var h in hits.Where(h => found.All(f => !f.PackageId.Equals(h.PackageId, StringComparison.OrdinalIgnoreCase))))
                output.WriteLine($"{h.PackageId}@{h.Version} {h.Title} unverified");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"nuget search unavailable: {ex.Message}");
        }
    }

    private static bool Has(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// Package ids already resolved under the plugins directory (one folder per package id).
    private static HashSet<string> Installed(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
}
