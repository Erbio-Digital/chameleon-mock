using System.Reflection;
using Chameleon.Sdk;

namespace Chameleon.Host.Plugins;

public static class PluginLoader
{
    private const string MockPrefix = "Chameleon.Mock.";

    // ponytail: one load at a time per process (connectors sharing a plugins dir write the same files);
    // a cross-process lock on the dir if several hosts ever share one.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// Resolves, verifies, loads and registers every requested connector before builder.Build().
    /// Throws PluginLoadException (startup fails) on any problem. Registers IReadOnlyList<LoadedPackage> as a singleton.
    public static async Task<IReadOnlyList<LoadedPackage>> LoadAsync(PluginOptions options, Catalog catalog, IServiceCollection services, ILogger log, CancellationToken ct)
    {
        var loaded = new List<LoadedPackage>();
        if (options.Requested.Count == 0)
            log.LogWarning("CHAMELEON_CONNECTORS is empty: starting without connectors");
        var resolver = new PackageResolver(options.NugetSources, options.PluginsDir, options.Offline, log);
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var requested in options.Requested)
            {
                var (main, pins, verified) = Plan(requested, options, catalog);
                var resolved = await resolver.ResolveAsync(main, pins, ct);
                var assembly = LoadMain(requested, resolved);
                Register(requested, main, assembly, services);
                loaded.Add(new LoadedPackage(assembly, main.Id, main.Version, verified));
                if (verified) log.LogInformation("connector {Requested}: {Package} {Version} loaded", requested, main.Id, main.Version);
                else log.LogWarning("connector {Requested}: {Package} {Version} loaded UNVERIFIED (no catalog hash check)", requested, main.Id, main.Version);
            }
        }
        finally { Gate.Release(); }
        services.AddSingleton<IReadOnlyList<LoadedPackage>>(loaded);
        return loaded;
    }

    /// Downloads and verifies one requested connector into the plugins directory (same rules as LoadAsync: catalog
    /// hashes, revocation, SDK compatibility) without loading it or touching DI. Throws PluginLoadException.
    public static async Task<(string PackageId, string Version, bool Verified)> InstallAsync(string requested, PluginOptions options, Catalog catalog, ILogger log, CancellationToken ct)
    {
        var (main, pins, verified) = Plan(requested, options, catalog);
        var resolver = new PackageResolver(options.NugetSources, options.PluginsDir, options.Offline, log);
        await Gate.WaitAsync(ct);
        try
        {
            var resolved = await resolver.ResolveAsync(main, pins, ct);
            CheckSdk(requested, resolved);
            return (main.Id, main.Version, verified);
        }
        finally { Gate.Release(); }
    }

    /// After builder.Build(): the connectors as DI built them, matched to their package by assembly. Throws
    /// PluginLoadException when two connectors share a Name. Connectors registered in process (no package) are
    /// checked for clashes but not returned.
    public static IReadOnlyList<LoadedConnector> Verify(IServiceProvider services)
    {
        var packages = services.GetRequiredService<IReadOnlyList<LoadedPackage>>();
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);
        var connectors = new List<LoadedConnector>();
        IReadOnlyList<IConnector> built;
        try { built = services.GetServices<IConnector>().ToList(); }
        catch (Exception ex) when (ex is not PluginLoadException)
        {
            var names = string.Join(", ", packages.Select(p => $"{p.PackageId} {p.Version}"));
            throw new PluginLoadException($"a connector failed to start (packages: {(names.Length == 0 ? "none" : names)}): {ex.Message}");
        }
        foreach (var connector in built)
        {
            var assembly = connector.GetType().Assembly;
            var package = packages.FirstOrDefault(p => p.Assembly == assembly);
            var origin = package is null ? $"{assembly.GetName().Name} (in process)" : $"{package.PackageId} {package.Version}";
            if (origins.TryGetValue(connector.Name, out var other))
                throw new PluginLoadException($"duplicate connector name '{connector.Name}': provided by {other} and {origin}");
            origins[connector.Name] = origin;
            if (package is not null) connectors.Add(new LoadedConnector(connector.Name, package.PackageId, package.Version, package.Verified));
        }
        return connectors;
    }

    /// What to resolve for one requested connector: a catalog short name (verified, closure pinned) or Id@version (unverified).
    private static (PackageRef Main, IReadOnlyDictionary<string, string>? Pins, bool Verified) Plan(string requested, PluginOptions options, Catalog catalog)
    {
        var at = requested.LastIndexOf('@');
        if (at >= 0)
        {
            var (id, version) = (requested[..at], requested[(at + 1)..]);
            if (id.Length == 0 || version.Length == 0)
                throw new PluginLoadException($"connector '{requested}' must be a catalog name or Id@version");
            if (!options.AllowUnverified)
                throw new PluginLoadException($"connector {requested} is not verified; set CHAMELEON_ALLOW_UNVERIFIED=true to load it");
            return (new PackageRef(id, version), null, false);
        }
        var entry = catalog.Find(requested)
            ?? throw new PluginLoadException($"unknown connector '{requested}'; use Id@version for unverified packages");
        if (entry.Revoked is not null)
            throw new PluginLoadException($"connector '{requested}' {entry.Version} is revoked: {entry.Revoked}");
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in entry.Closure)
            pins[p.Id] = p.Sha512 ?? throw new PluginLoadException($"connector '{requested}': catalog has no hash for {p.Id} {p.Version}");
        return (new PackageRef(entry.PackageId, entry.Version), pins, true);
    }

    /// The main assembly is the dll named after the package id without "Mock." (Chameleon.Mock.Connectors.X ->
    /// Chameleon.Connectors.X.dll), else the single dll of the main package. It must reference a compatible Chameleon.Sdk.
    private static Assembly LoadMain(string requested, ResolvedConnector resolved)
    {
        var (_, file) = CheckSdk(requested, resolved);
        try { return new ConnectorLoadContext(requested, resolved.Directory).LoadMain(file); }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            throw new PluginLoadException($"connector {requested}: cannot load {file} from {resolved.Main.Id} {resolved.Main.Version}: {ex.Message}");
        }
    }

    /// Finds the main assembly and checks its Chameleon.Sdk reference; returns its path and file name.
    private static (string Path, string File) CheckSdk(string requested, ResolvedConnector resolved)
    {
        var id = resolved.Main.Id;
        var file = (id.StartsWith(MockPrefix, StringComparison.Ordinal) ? "Chameleon." + id[MockPrefix.Length..] : id) + ".dll";
        if (!File.Exists(Path.Combine(resolved.Directory, file)))
            file = resolved.MainAssemblies.Count == 1 ? resolved.MainAssemblies[0]
                : throw new PluginLoadException($"connector {requested}: cannot find the main assembly of {id} (expected {file})");
        var path = Path.Combine(resolved.Directory, file);
        try
        {
            var sdk = SdkCompatibility.ReferencedSdk(path)
                ?? throw new PluginLoadException($"connector {requested}: {file} does not reference Chameleon.Sdk");
            var host = typeof(IConnector).Assembly.GetName().Version!;
            if (!SdkCompatibility.IsCompatible(host, sdk))
                throw new PluginLoadException($"connector {requested} was built for Chameleon.Sdk {sdk.ToString(3)}; this Chameleon needs {SdkCompatibility.Range(host)}");
            return (path, file);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            throw new PluginLoadException($"connector {requested}: cannot load {file} from {id} {resolved.Main.Version}: {ex.Message}");
        }
    }

    /// Runs the package's modules and registers its connectors.
    private static void Register(string requested, PackageRef main, Assembly assembly, IServiceCollection services)
    {
        try
        {
            if (ConnectorDiscovery.Register(assembly, services).Count == 0)
                throw new PluginLoadException($"connector {requested}: package {main.Id} {main.Version} contains no IConnector");
        }
        catch (PluginLoadException) { throw; }
        catch (ReflectionTypeLoadException ex)
        {
            var reasons = string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct());
            throw new PluginLoadException($"connector {requested}: package {main.Id} {main.Version} failed to load types: {reasons}");
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($"connector {requested}: package {main.Id} {main.Version} failed to register: {ex.GetBaseException().Message}");
        }
    }
}
