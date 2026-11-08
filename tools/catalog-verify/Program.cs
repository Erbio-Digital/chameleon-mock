using System.Text.Json;
using System.Text.RegularExpressions;
using Chameleon.CatalogVerify;
using Chameleon.Host.Plugins;

// catalog-verify --catalog <path> --sources <src,...> --providers <dir> --host <Chameleon.Host.dll> [--only <name>] [--skip-network-checks] [--skip-smoke]
var options = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--skip-network-checks" or "--skip-smoke") flags.Add(args[i]);
    else if (args[i] is "--catalog" or "--sources" or "--providers" or "--host" or "--only")
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"missing value for {args[i]}"); return 2; }
        options[args[i]] = args[++i];
    }
    else { Console.Error.WriteLine($"unknown argument {args[i]}"); return 2; }
}
foreach (var required in new[] { "--catalog", "--sources", "--providers" })
    if (!options.ContainsKey(required)) { Console.Error.WriteLine($"missing {required}"); return 2; }
if (!flags.Contains("--skip-smoke") && !options.ContainsKey("--host")) { Console.Error.WriteLine("missing --host (or --skip-smoke)"); return 2; }

Catalog catalog;
try { catalog = Catalog.Parse(File.ReadAllText(options["--catalog"])); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"cannot read the catalog {options["--catalog"]}: {ex.Message}");
    return 2;
}
var entries = options.TryGetValue("--only", out var only) ? catalog.Entries.Where(e => e.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList() : catalog.Entries.ToList();
if (entries.Count == 0 && only is null) { Console.WriteLine("the catalog is empty: nothing to verify"); return 0; }
if (entries.Count == 0) { Console.Error.WriteLine(only is null ? "the catalog has no entries" : $"no catalog entry named '{only}'"); return 2; }
// Revoked entries are not verified: the revocation is the final decision on them.
entries = entries.Where(e => e.Revoked is null).ToList();
if (entries.Count == 0) { Console.WriteLine("every selected catalog entry is revoked: nothing to verify"); return 0; }

// Every entry is resolved from the sources (feed URLs or folders) into a temp folder; the checks run on that copy.
// A relative folder is made absolute: NuGet does not resolve it against the current directory.
var sources = options["--sources"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => Uri.TryCreate(s, UriKind.Absolute, out _) ? s : Path.GetFullPath(s)).ToList();
var temp = Directory.CreateTempSubdirectory("chameleon-catalog-verify-").FullName;
var packagesDir = Path.Combine(temp, "packages");
var resolver = new PackageResolver(sources, temp, offline: false);
var findings = new List<VerifyFinding>();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
try
{
    foreach (var entry in entries)
    {
        try { await resolver.ResolveAsync(new(entry.PackageId, entry.Version), null, default); }
        catch (PluginLoadException ex)
        {
            findings.Add(new(entry.Name, "hash", $"{entry.PackageId} {entry.Version} not found in the sources: {ex.Message}"));
            Console.WriteLine($"{entry.Name} {entry.Version}: FAIL");
            continue;
        }

        // An unexpected failure (bad assembly, corrupt package, unexpected Host answer) fails the entry, not the run.
        async Task Guard(Func<Task<IEnumerable<VerifyFinding>>> check)
        {
            try { findings.AddRange(await check()); }
            catch (Exception ex) { findings.Add(new(entry.Name, "error", $"{ex.GetType().Name}: {ex.Message}")); }
        }

        await Guard(() => Task.FromResult<IEnumerable<VerifyFinding>>(CatalogVerifier.CheckEntry(entry, packagesDir, options["--providers"])));
        if (!flags.Contains("--skip-network-checks"))
            await Guard(async () => await CatalogVerifier.CheckRepositoryReachableAsync(entry, http, default) is { } unreachable ? [unreachable] : []);
        if (!flags.Contains("--skip-smoke"))
            await Guard(async () => await ContractSmoke.RunAsync(entry, options["--host"], packagesDir, options["--catalog"], TimeSpan.FromSeconds(60), default));
        Console.WriteLine($"{entry.Name} {entry.Version}: {(findings.Any(f => f.Connector == entry.Name) ? "FAIL" : "ok")}");
    }
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
// One finding per line: install output and Host logs are multi-line.
foreach (var f in findings) Console.WriteLine($"{f.Connector} {f.Check}: {Regex.Replace(f.Message, @"\s*[\r\n]+\s*", " ")}");
return findings.Count == 0 ? 0 : 1;
