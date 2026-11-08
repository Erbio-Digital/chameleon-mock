using Chameleon.Host.Plugins;

namespace Chameleon.CatalogTool;

public static class CatalogMerge
{
    /// Downloads each Id@version (with its closure) from the sources, rebuilds the entries and merges them by name into the catalog file.
    public static async Task<int> FromSourcesAsync(IReadOnlyList<string> sources, IReadOnlyList<string> packages, string providersDir, string catalogPath, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("chameleon-catalog-").FullName;
        try
        {
            var resolver = new PackageResolver(sources, temp, offline: false);
            foreach (var package in packages)
            {
                var at = package.LastIndexOf('@');
                if (at <= 0 || at == package.Length - 1) throw new ArgumentException($"'{package}' is not Id@version");
                await resolver.ResolveAsync(new(package[..at], package[(at + 1)..]), null, ct);
            }
            var fresh = CatalogBuilder.Build(Path.Combine(temp, "packages"), providersDir);
            var merged = Catalog.Parse(File.ReadAllText(catalogPath)).Entries
                .Where(e => !fresh.Any(f => f.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
                .Concat(fresh).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            File.WriteAllText(catalogPath, Catalog.Serialize(merged) + "\n");
            Console.WriteLine($"{fresh.Count} entr{(fresh.Count == 1 ? "y" : "ies")} merged into {catalogPath} ({merged.Count} total)");
            return 0;
        }
        finally { Directory.Delete(temp, true); }
    }
}
