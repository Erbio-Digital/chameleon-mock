namespace Chameleon.Host.Plugins;

public sealed record PluginOptions(IReadOnlyList<string> Requested, bool AllowUnverified, bool Offline, string PluginsDir,
    string CatalogUrl, IReadOnlyList<string> NugetSources)
{
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/Erbio-Digital/chameleon-mock/main/catalog/connectors.json";
    public const string DefaultNugetSource = "https://api.nuget.org/v3/index.json";

    public static PluginOptions From(IConfiguration config)
    {
        var sources = List(config["CHAMELEON_NUGET_SOURCES"]);
        var inContainer = string.Equals(config["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
        return new(
            List(config["CHAMELEON_CONNECTORS"]),
            Flag(config["CHAMELEON_ALLOW_UNVERIFIED"]),
            Flag(config["CHAMELEON_OFFLINE"]),
            NonEmpty(config["CHAMELEON_PLUGINS_DIR"]) ?? (inContainer ? "/plugins" : "./plugins"),
            NonEmpty(config["CHAMELEON_CATALOG_URL"]) ?? DefaultCatalogUrl,
            sources.Count > 0 ? sources : [DefaultNugetSource]);
    }

    private static List<string> List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool Flag(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
