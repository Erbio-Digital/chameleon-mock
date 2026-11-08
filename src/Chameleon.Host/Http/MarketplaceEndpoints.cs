using Chameleon.Host.Plugins;

namespace Chameleon.Host.Http;

/// Read-only marketplace: the verified catalog and the community packages on NuGet. Nothing here installs.
public static class MarketplaceEndpoints
{
    private const string Prefix = ChameleonEndpoints.ControlPrefix + "/marketplace";

    public static void MapMarketplace(this IEndpointRouteBuilder routes)
    {
        routes.MapGet($"{Prefix}/verified", async (MarketplaceCatalog marketplace, [Microsoft.AspNetCore.Mvc.FromServices] IReadOnlyList<LoadedPackage> plugins, CancellationToken ct) =>
        {
            var catalog = await marketplace.GetAsync(ct);
            return Results.Ok(catalog.Entries.Select(e =>
            {
                var installed = plugins.FirstOrDefault(p => p.PackageId.Equals(e.PackageId, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    e.Name, e.PackageId, e.Version, e.Title, e.Description, e.Provider, e.Repository, e.License, e.Sdk, e.Revoked,
                    installed = installed is not null,
                    installedVersion = installed?.Version,
                };
            }));
        });

        routes.MapGet($"{Prefix}/community", async (string? q, CommunitySearch search, MarketplaceCatalog marketplace, PluginOptions options, CancellationToken ct) =>
        {
            var catalog = await marketplace.GetAsync(ct);
            try
            {
                var packages = await search.SearchAsync(q ?? "", ct);
                return Results.Ok(new
                {
                    offline = options.Offline,
                    error = (string?)null,
                    packages = packages.Select(p => new
                    {
                        p.PackageId, p.Version, p.Title, p.Description, p.Repository, p.License, p.ProjectUrl,
                        inCatalog = catalog.Entries.Any(e => e.PackageId.Equals(p.PackageId, StringComparison.OrdinalIgnoreCase)),
                    }),
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new { offline = options.Offline, error = $"NuGet search unavailable: {ex.Message}", packages = Array.Empty<object>() });
            }
        });
    }
}
