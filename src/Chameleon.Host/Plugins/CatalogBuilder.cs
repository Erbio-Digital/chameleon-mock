using NuGet.Frameworks;
using NuGet.Packaging;

namespace Chameleon.Host.Plugins;

/// Builds catalog entries from a folder of .nupkg files: every package tagged chameleon-connector and
/// chameleon-name:<name>, with its closure (main package included) pinned by SHA-512. Used by tools/catalog and the tests.
public static class CatalogBuilder
{
    private static readonly NuGetFramework Target = NuGetFramework.Parse("net8.0");

    /// ponytail: packages are indexed by id.version in the one folder; a dependency not in the folder fails the build.
    public static IReadOnlyList<CatalogEntry> Build(string packagesDir, string? providersDir = null)
    {
        var entries = new List<CatalogEntry>();
        foreach (var file in Directory.GetFiles(packagesDir, "*.nupkg"))
        {
            using var reader = new PackageArchiveReader(file);
            var nuspec = reader.NuspecReader;
            var tags = nuspec.GetTags().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!tags.Contains("chameleon-connector")) continue;
            var id = nuspec.GetId();
            var name = tags.FirstOrDefault(t => t.StartsWith("chameleon-name:"))?["chameleon-name:".Length..]
                ?? throw new InvalidOperationException($"{id}: tagged chameleon-connector but has no chameleon-name:<name> tag");
            if (!ProviderSheet.IsValidName(name))
                throw new InvalidOperationException($"{id}: connector name '{name}' must match ^[a-z0-9][a-z0-9-]*$");
            var version = nuspec.GetVersion().ToNormalizedString();
            entries.Add(new CatalogEntry(name, id, version,
                Title: string.IsNullOrWhiteSpace(nuspec.GetTitle()) ? id : nuspec.GetTitle(),
                Description: nuspec.GetDescription(),
                Provider: (providersDir is null ? null : ProviderSheet.ProviderUrl(providersDir, name)) ?? nuspec.GetProjectUrl() ?? "",
                Repository: nuspec.GetRepositoryMetadata().Url ?? nuspec.GetProjectUrl() ?? "",
                License: nuspec.GetLicenseMetadata()?.License ?? "",
                Sdk: ReadSdkRange(reader, id), Closure: Closure(packagesDir, id, version), Revoked: null));
        }
        var dup = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new InvalidOperationException($"duplicate connector name '{dup.Key}' in {packagesDir}: {string.Join(", ", dup.Select(e => $"{e.PackageId} {e.Version}"))} (clean old packages from the folder)");
        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static List<PackageRef> Closure(string dir, string id, string version)
    {
        var closure = new List<PackageRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var queue = new Queue<(string Id, string Version)>();
        queue.Enqueue((id, version));
        while (queue.Count > 0)
        {
            var (pid, pver) = queue.Dequeue();
            var path = Path.Combine(dir, $"{pid}.{pver}.nupkg");
            if (!File.Exists(path)) throw new FileNotFoundException($"{pid} {pver} not found in {dir}");
            closure.Add(new PackageRef(pid, pver, PackageResolver.Sha512(path)));
            using var r = new PackageArchiveReader(path);
            var group = NuGetFrameworkUtility.GetNearest(r.GetPackageDependencies(), Target, g => g.TargetFramework);
            foreach (var dep in group?.Packages ?? [])
            {
                if (PackageResolver.IsSharedPackage(dep.Id) || !seen.Add(dep.Id)) continue;
                queue.Enqueue((dep.Id, (dep.VersionRange.MinVersion ?? throw new InvalidOperationException($"{pid}: dependency {dep.Id} has no minimum version")).ToNormalizedString()));
            }
        }
        return closure;
    }

    // The main package's assembly that references Chameleon.Sdk, as an informative range ("0.1.x" / "1.x").
    private static string ReadSdkRange(PackageArchiveReader reader, string id)
    {
        var group = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), Target, g => g.TargetFramework);
        foreach (var item in group?.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var tmp = Path.GetTempFileName();
            try
            {
                using (var src = reader.GetStream(item)) using (var dst = File.Create(tmp)) src.CopyTo(dst);
                var v = SdkCompatibility.ReferencedSdk(tmp);
                if (v is not null) return SdkCompatibility.Range(v);
            }
            finally { File.Delete(tmp); }
        }
        throw new InvalidOperationException($"{id}: no assembly references Chameleon.Sdk");
    }
}
