using Chameleon.Sdk;
using NuGet.Packaging;

using Chameleon.Host.Plugins;

namespace Chameleon.CatalogVerify;

public sealed record VerifyFinding(string Connector, string Check, string Message);

/// What a catalog entry must satisfy before a maintainer reviews its source.
public static class CatalogVerifier
{
    public static readonly IReadOnlySet<string> OsiLicenses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Apache-2.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MPL-2.0",
        "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later",
        "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "EPL-2.0",
    };

    public static IReadOnlyList<VerifyFinding> CheckEntry(CatalogEntry entry, string packagesDir, string providersDir)
    {
        var findings = new List<VerifyFinding>();
        void Add(string check, string message) => findings.Add(new(entry.Name, check, message));

        foreach (var package in entry.Closure)
        {
            var path = Path.Combine(packagesDir, $"{package.Id}.{package.Version}.nupkg");
            if (!File.Exists(path)) { Add("hash", $"{package.Id} {package.Version} not found in the sources"); continue; }
            if (!string.Equals(PackageResolver.Sha512(path), package.Sha512, StringComparison.Ordinal))
                Add("hash", $"{package.Id} {package.Version}: SHA-512 differs from the catalog");
        }

        CatalogEntry? regenerated = null;
        try
        {
            regenerated = CatalogBuilder.Build(packagesDir).FirstOrDefault(e =>
                e.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) && e.Version == entry.Version);
            if (regenerated is null) Add("closure", $"{entry.PackageId} {entry.Version} is not a connector package in the sources");
            else
            {
                var expected = regenerated.Closure.Select(c => $"{c.Id}@{c.Version}").Order(StringComparer.OrdinalIgnoreCase).ToList();
                var actual = entry.Closure.Select(c => $"{c.Id}@{c.Version}").Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                    Add("closure", $"catalog closure [{string.Join(", ", actual)}] differs from the package's [{string.Join(", ", expected)}]");
                foreach (var finding in CheckSdk(regenerated, packagesDir)) Add("sdk", finding);
                // The package's .nuspec is the source of truth; the catalog only mirrors it.
                foreach (var (field, catalogValue, packageValue) in new[]
                {
                    ("name", entry.Name, regenerated.Name), ("license", entry.License, regenerated.License),
                    ("repository", entry.Repository, regenerated.Repository), ("sdk", entry.Sdk, regenerated.Sdk),
                })
                    if (!string.Equals(catalogValue, packageValue, StringComparison.Ordinal))
                        Add("metadata", $"{field}: catalog says '{catalogValue}', the package says '{packageValue}'");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException) { Add("closure", ex.Message); }

        // Both the catalog's and the package's values must pass: neither can hide a bad one.
        foreach (var license in new[] { entry.License, regenerated?.License }.OfType<string>().Distinct())
            if (!OsiLicenses.Contains(license)) Add("license", $"'{license}' is not an accepted OSI license expression");
        foreach (var repository in new[] { entry.Repository, regenerated?.Repository }.OfType<string>().Distinct())
            if (!Uri.TryCreate(repository, UriKind.Absolute, out var repo) || repo.Scheme != Uri.UriSchemeHttps)
                Add("repository", $"'{repository}' is not an https URL");

        var sheet = Path.Combine(providersDir, $"{entry.Name}.md");
        if (!File.Exists(sheet)) Add("provider-sheet", $"missing {Path.GetFileName(providersDir)}/{entry.Name}.md");
        else if (ProviderSheet.ProviderUrl(providersDir, entry.Name) is not { } providerUrl) Add("provider-sheet", "the Sources section needs at least one https:// link");
        else if (!string.Equals(entry.Provider, providerUrl, StringComparison.Ordinal))
            Add("provider-sheet", $"provider: catalog says '{entry.Provider}', the sheet says '{providerUrl}'");

        return findings;
    }

    /// Public means reachable without credentials: HEAD, then GET as a fallback.
    public static async Task<VerifyFinding?> CheckRepositoryReachableAsync(CatalogEntry entry, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, entry.Repository), ct);
            if (head.IsSuccessStatusCode) return null;
            using var get = await http.GetAsync(entry.Repository, HttpCompletionOption.ResponseHeadersRead, ct);
            return get.IsSuccessStatusCode ? null : new(entry.Name, "repository", $"{entry.Repository} answered {(int)get.StatusCode} without credentials");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new(entry.Name, "repository", $"{entry.Repository} is not reachable: {ex.Message}");
        }
    }

    /// Every assembly of the closure that references the SDK must be compatible with this Core, not only the main one.
    private static IEnumerable<string> CheckSdk(CatalogEntry entry, string packagesDir)
    {
        var host = typeof(IConnector).Assembly.GetName().Version!;
        foreach (var package in entry.Closure)
        {
            using var reader = new PackageArchiveReader(Path.Combine(packagesDir, $"{package.Id}.{package.Version}.nupkg"));
            foreach (var item in reader.GetFiles().Where(f => f.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var src = reader.GetStream(item)) using (var dst = File.Create(tmp)) src.CopyTo(dst);
                    var referenced = SdkCompatibility.ReferencedSdk(tmp);
                    if (referenced is not null && !SdkCompatibility.IsCompatible(host, referenced))
                        yield return $"{package.Id}/{Path.GetFileName(item)} references Chameleon.Sdk {referenced.ToString(3)}; this Chameleon needs {SdkCompatibility.Range(host)}";
                }
                finally { File.Delete(tmp); }
            }
        }
    }
}
