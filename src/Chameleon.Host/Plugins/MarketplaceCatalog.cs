namespace Chameleon.Host.Plugins;

/// The catalog the marketplace shows: the one loaded at startup, or loaded once on first use when
/// no connector was requested (D1).
public sealed class MarketplaceCatalog(PluginOptions options, Catalog? atStartup, ILogger<MarketplaceCatalog> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Catalog? _catalog = atStartup;

    public async Task<Catalog> GetAsync(CancellationToken ct)
    {
        if (_catalog is not null) return _catalog;
        await _gate.WaitAsync(ct);
        try
        {
            using var http = new HttpClient();
            return _catalog ??= await Catalog.LoadAsync(options.CatalogUrl, options.Offline, http, log, ct);
        }
        finally { _gate.Release(); }
    }
}
