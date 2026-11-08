using System.Collections.Concurrent;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Chameleon.Host.Plugins;

public sealed record CommunityPackage(string PackageId, string Version, string Title, string Description,
    string? Repository, string? License, string? ProjectUrl);

/// Connectors published to NuGet with the chameleon-connector tag: unverified, shown with a warning.
public sealed class CommunitySearch
{
    public const string Tag = "chameleon-connector";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private const int MaxTermLength = 100;
    private const int MaxCached = 200;
    private readonly PluginOptions _options;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<CommunityPackage>>> _query;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<CommunityPackage> Packages)> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal int CachedCount => _cache.Count;

    public CommunitySearch(PluginOptions options)
    {
        _options = options;
        _query = QueryNuGetAsync;
    }

    internal CommunitySearch(PluginOptions options, Func<string, CancellationToken, Task<IReadOnlyList<CommunityPackage>>> query)
    {
        _options = options;
        _query = query;
    }

    /// Empty offline. Network errors propagate (callers decide how to show them).
    public async Task<IReadOnlyList<CommunityPackage>> SearchAsync(string term, CancellationToken ct)
    {
        if (_options.Offline) return [];
        term = term.Trim();
        if (term.Length > MaxTermLength) term = term[..MaxTermLength];
        if (_cache.TryGetValue(term, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Packages;
        var packages = await _query(term, ct);
        foreach (var stale in _cache.Where(e => DateTimeOffset.UtcNow - e.Value.At >= CacheFor).Select(e => e.Key)) _cache.TryRemove(stale, out _);
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[term] = (DateTimeOffset.UtcNow, packages);
        return packages;
    }

    private async Task<IReadOnlyList<CommunityPackage>> QueryNuGetAsync(string term, CancellationToken ct)
    {
        var found = new List<CommunityPackage>();
        foreach (var source in _options.NugetSources.Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            var search = await Repository.Factory.GetCoreV3(source).GetResourceAsync<PackageSearchResource>(ct);
            var hits = await search.SearchAsync($"tags:{Tag} {term}".Trim(), new SearchFilter(false), 0, 20, NuGet.Common.NullLogger.Instance, ct);
            found.AddRange(hits.Select(h => new CommunityPackage(h.Identity.Id, h.Identity.Version.ToNormalizedString(),
                string.IsNullOrWhiteSpace(h.Title) ? h.Identity.Id : h.Title, h.Description ?? "",
                null, h.LicenseMetadata?.License, h.ProjectUrl?.ToString())));
        }
        return found.DistinctBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
