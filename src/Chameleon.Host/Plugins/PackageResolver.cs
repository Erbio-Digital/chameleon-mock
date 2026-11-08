using System.Security.Cryptography;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Chameleon.Host.Plugins;

public sealed class PackageResolver(IReadOnlyList<string> sources, string pluginsDir, bool offline, Microsoft.Extensions.Logging.ILogger? log = null)
{
    private static readonly NuGetFramework Target = NuGetFramework.Parse("net8.0");
    private static readonly string[] SharedPrefixes = ["Microsoft.AspNetCore.", "Microsoft.Extensions.", "System."];

    /// Downloads (or reuses from cache) the main package and its dependency closure, skipping shared packages,
    /// verifies each package's SHA-512 when a hash is known (main ref hash, or the pinned closure map), and
    /// flattens lib/net8.0 (or the best compatible lib folder) of every package into {pluginsDir}/{id}/{version}/lib.
    /// Throws PluginLoadException with a message naming the package on any failure.
    /// ponytail: sequential download, first source that has the package wins; parallelise if closures grow large.
    public async Task<ResolvedConnector> ResolveAsync(PackageRef main, IReadOnlyDictionary<string, string>? pinnedClosureSha512, CancellationToken ct)
    {
        var pins = pinnedClosureSha512 is null ? null
            : new Dictionary<string, string>(pinnedClosureSha512, StringComparer.OrdinalIgnoreCase);
        var libDir = Path.Combine(pluginsDir, main.Id, main.Version, "lib");
        var closure = new List<PackageRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { main.Id };
        var queue = new Queue<PackageRef>();
        queue.Enqueue(main);
        PackageRef? resolvedMain = null;
        IReadOnlyList<string> mainAssemblies = [];
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { Directory.CreateDirectory(libDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginLoadException($"Cannot create the plugins directory {libDir}: {ex.Message}");
        }

        while (queue.Count > 0)
        {
            var pkg = queue.Dequeue();
            try
            {
                string? expected = pkg == main ? main.Sha512 : null;
                if (pins is not null)
                {
                    if (!pins.TryGetValue(pkg.Id, out var pinned))
                        throw new PluginLoadException($"Package {pkg.Id} {pkg.Version}: not in the pinned closure");
                    expected ??= pinned;
                }
                var cachedBefore = File.Exists(CachePath(pkg));
                var path = await EnsureCachedAsync(pkg, ct);
                var actual = PackageResolver.Sha512(path);
                if (expected is not null && expected != actual && cachedBefore)
                {
                    // A stale or poisoned cache entry: fail closed offline, otherwise drop it and verify a fresh download once.
                    if (offline)
                        throw new PluginLoadException($"Package {pkg.Id} {pkg.Version}: cached package hash mismatch (expected {expected}, got {actual}); clear the plugins cache ({Path.GetDirectoryName(path)}) and retry online");
                    File.Delete(path);
                    path = await EnsureCachedAsync(pkg, ct);
                    actual = PackageResolver.Sha512(path);
                }
                if (expected is not null && expected != actual)
                    throw new PluginLoadException($"Package {pkg.Id} {pkg.Version}: hash mismatch (expected {expected}, got {actual})");
                var resolved = pkg with { Sha512 = actual };
                if (pkg == main) resolvedMain = resolved; else closure.Add(resolved);

                using var reader = new PackageArchiveReader(path);
                var files = Flatten(reader, libDir, pkg.Id, written);
                if (pkg == main) mainAssemblies = files;
                var group = NuGetFrameworkUtility.GetNearest(reader.GetPackageDependencies(), Target, g => g.TargetFramework);
                foreach (var dep in group?.Packages ?? [])
                {
                    if (IsSharedPackage(dep.Id) || !seen.Add(dep.Id)) continue;
                    var min = dep.VersionRange.MinVersion ?? throw new PluginLoadException($"Package {pkg.Id}: dependency {dep.Id} has no minimum version");
                    queue.Enqueue(new PackageRef(dep.Id, min.ToNormalizedString()));
                }
            }
            catch (PluginLoadException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new PluginLoadException($"Package {pkg.Id} {pkg.Version}: {ex.Message}");
            }
        }
        var unseen = pins?.Keys.Where(k => !seen.Contains(k)).ToList();
        if (unseen is { Count: > 0 })
            throw new PluginLoadException($"Pinned package(s) not in the resolved closure of {main.Id}: {string.Join(", ", unseen)}");
        return new ResolvedConnector(resolvedMain!, closure, libDir, mainAssemblies);
    }

    /// SHA-512 (base64, the NuGet convention) of a .nupkg file.
    public static string Sha512(string nupkgPath)
    {
        using var fs = File.OpenRead(nupkgPath);
        return Convert.ToBase64String(SHA512.HashData(fs));
    }

    /// Packages provided by the host (Engine, Sdk, ASP.NET Core, Microsoft.Extensions, System): never part of a connector closure.
    public static bool IsSharedPackage(string id) =>
        id.Equals("Chameleon.Mock.Engine", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Chameleon.Mock.Sdk", StringComparison.OrdinalIgnoreCase)
        || SharedPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private string CachePath(PackageRef pkg) => Path.Combine(pluginsDir, "packages", $"{pkg.Id}.{pkg.Version}.nupkg");

    private async Task<string> EnsureCachedAsync(PackageRef pkg, CancellationToken ct)
    {
        var path = CachePath(pkg);
        var cacheDir = Path.GetDirectoryName(path)!;
        if (File.Exists(path)) return path;
        if (offline) throw new PluginLoadException($"Package {pkg.Id} {pkg.Version} is not in cache and CHAMELEON_OFFLINE=true");
        Directory.CreateDirectory(cacheDir);
        var version = NuGetVersion.Parse(pkg.Version);
        using var cache = new SourceCacheContext { NoCache = true };
        foreach (var source in sources)
        {
            var res = await Repository.Factory.GetCoreV3(source).GetResourceAsync<FindPackageByIdResource>(ct);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                bool ok;
                await using (var fs = File.Create(tmp))
                    ok = await res.CopyNupkgToStreamAsync(pkg.Id, version, fs, cache, NullLogger.Instance, ct);
                if (ok) { File.Move(tmp, path, overwrite: true); return path; }
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        throw new PluginLoadException($"Package {pkg.Id} {pkg.Version} was not found in any configured source");
    }

    /// Copies the package's lib dlls into libDir and returns their file names. A file already there with the same
    /// bytes is left alone: id/version are immutable and hash-checked, and an earlier load in this process may hold it open.
    private List<string> Flatten(PackageArchiveReader reader, string libDir, string id, Dictionary<string, string> written)
    {
        var files = new List<string>();
        var group = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), Target, g => g.TargetFramework);
        foreach (var item in group?.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var name = Path.GetFileName(item);
            if (written.TryGetValue(name, out var owner))
            {
                if (log is null) Console.Error.WriteLine($"Plugin dll {name} from {id} ignored: already provided by {owner}");
                else log.LogWarning("Plugin dll {Dll} from {Package} ignored: already provided by {Owner}", name, id, owner);
                continue;
            }
            written[name] = id;
            files.Add(name);
            byte[] bytes;
            using (var src = reader.GetStream(item)) using (var ms = new MemoryStream()) { src.CopyTo(ms); bytes = ms.ToArray(); }
            var dst = Path.Combine(libDir, name);
            if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes)) continue;
            // Temp file in the same dir, then a move: nobody ever sees a half-written dll.
            var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, dst, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        return files;
    }
}
