using System.Reflection;
using System.Text.Json;

namespace Chameleon.Host.Plugins;

public sealed record CatalogEntry(string Name, string PackageId, string Version, string Title, string Description,
    string Provider, string Repository, string License, string Sdk, IReadOnlyList<PackageRef> Closure, string? Revoked);

public sealed class Catalog(IReadOnlyList<CatalogEntry> entries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<CatalogEntry> Entries => entries;

    public CatalogEntry? Find(string name) =>
        entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// Throws JsonException on a malformed catalog: a null entry, a missing required field or a duplicate name.
    public static Catalog Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<CatalogEntry>>(json, Json) ?? throw new JsonException("catalog is null");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i] ?? throw new JsonException($"catalog entry {i} is null");
            if (string.IsNullOrWhiteSpace(e.Name)) throw new JsonException($"catalog entry {i} has no name");
            if (string.IsNullOrWhiteSpace(e.PackageId)) throw new JsonException($"catalog entry '{e.Name}' has no packageId");
            if (string.IsNullOrWhiteSpace(e.Version)) throw new JsonException($"catalog entry '{e.Name}' has no version");
            if (e.Closure is null || e.Closure.Count == 0 || e.Closure.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Version)))
                throw new JsonException($"catalog entry '{e.Name}' has no valid closure");
            if (!names.Add(e.Name)) throw new JsonException($"catalog has duplicate connector name '{e.Name}'");
        }
        return new(list);
    }

    public static string Serialize(IEnumerable<CatalogEntry> entries) => JsonSerializer.Serialize(entries, Json);

    public static Catalog Embedded()
    {
        using var s = typeof(Catalog).Assembly.GetManifestResourceStream("catalog.json")
            ?? throw new InvalidOperationException("catalog.json is not embedded in the host assembly");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    /// The catalog at url (https, http on loopback, or file:// for an operator-provided local file; 5 s timeout),
    /// embedded on any failure, when empty, or when offline (a local file is still read offline). Logs which one was used.
    public static async Task<Catalog> LoadAsync(string url, bool offline, HttpClient http, ILogger log, CancellationToken ct)
    {
        var isFile = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile;
        string reason;
        if (offline && !isFile) reason = "offline";
        else if (!isFile && !IsAllowedUrl(url)) reason = "remote url must be https (http only for loopback) or file://";
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var json = isFile ? await File.ReadAllTextAsync(uri!.LocalPath, cts.Token) : await http.GetStringAsync(url, cts.Token);
                var catalog = Parse(json);
                if (catalog.Entries.Count > 0)
                {
                    log.LogInformation("catalog: {Source}", isFile ? "file" : "remote");
                    return catalog;
                }
                reason = "catalog is empty";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { reason = ex.Message; }
        }
        log.LogInformation("catalog: embedded ({Reason})", reason);
        return Embedded();
    }

    private static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));
}
