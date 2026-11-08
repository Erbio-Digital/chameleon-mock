using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Chameleon.Engine;

/// LGPD guard for everything the journal and the views store or show: personal data is masked,
/// secrets are redacted, sensitive headers never stored, and bodies are truncated only after masking.
/// Fields are classified by name pattern, so a connector's new field (cpfCnpjPagador, refresh_token)
/// is covered without listing it here.
public static partial class PayloadSanitizer
{
    public const int MaxBodyChars = 64 * 1024;
    public const string Redacted = "[redacted]";

    private enum Kind { None, Personal, Secret }

    // Exact names too short or too generic to match by substring ("cep" is inside "excecao").
    private static readonly HashSet<string> PersonalExact = new(
        new[] { "cep", "ddd", "numero", "name", "chave", "uf", "infoPagador", "numeroInscricao", "numeroFilial", "numeroControle" }
            .Concat((Environment.GetEnvironmentVariable("CHAMELEON_MASK_FIELDS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("cpf|cnpj|email|telefone|phone|celular|endereco|logradouro|bairro|complemento|document|razaosocial|nome|chavepix|agencia|conta",
        RegexOptions.IgnoreCase)]
    private static partial Regex PersonalPattern();

    [GeneratedRegex("token|secret|password|senha|api_?key|api-key|private_?key|keypem|pfx|csr|client_assertion", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("auth|token|secret|key|cookie|senha|password|signature", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveHeader();

    // Free text written by people (helpdesk comments): an e-mail or a CPF/CNPJ inside is masked, the rest kept.
    private static readonly HashSet<string> FreeText = new(["body", "html_body", "plain_body", "subject", "description"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+|\b\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}\b|\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b")]
    private static partial Regex PersonalInText();

    // Generic {id, value} pairs (helpdesk custom fields) whose id a connector declared personal.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PersonalFieldIds = new();

    /// A connector declares the ids of its provider's {id, value} fields that hold personal data.
    public static void MaskFieldIds(params string[] ids)
    {
        foreach (var id in ids) PersonalFieldIds[id] = 0;
    }

    /// Bodies that are not text (an upload) go on record by size only.
    public static bool IsText(string? contentType) =>
        string.IsNullOrEmpty(contentType)
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    public static string Binary(long bytes) => bytes == 0 ? "" : $"<binary {bytes} bytes>";

    private static Kind Classify(string field)
    {
        var name = field[(field.LastIndexOf(':') + 1)..]; // XML namespace prefix
        if (SecretPattern().IsMatch(name)) return Kind.Secret;
        return PersonalExact.Contains(name) || PersonalPattern().IsMatch(name) ? Kind.Personal : Kind.None;
    }

    private static string Apply(Kind kind, string value) => kind switch
    {
        Kind.Secret => Redacted,
        Kind.Personal => Mask(value),
        _ => value,
    };

    /// Returns the sanitized body and whether it was truncated.
    public static (string? Body, bool Truncated) Body(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (body, false);
        var masked = MaskJson(body) ?? MaskForm(body) ?? MaskLoose(body);
        return masked.Length > MaxBodyChars ? (masked[..MaxBodyChars], true) : (masked, false);
    }

    public static Dictionary<string, string> Headers(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.ToDictionary(h => h.Key, h => SensitiveHeader().IsMatch(h.Key) ? Redacted : h.Value, StringComparer.OrdinalIgnoreCase);

    /// A query string ("?a=1&b=2") with every sensitive value masked.
    public static string Query(string? query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return query ?? "";
        return "?" + string.Join('&', query.TrimStart('?').Split('&').Select(MaskPair));
    }

    /// A URL as it may be shown or journaled: no userinfo and no query values (the app under test
    /// often carries its webhook api key in the query).
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url ?? "";
        var query = uri.Query.Length > 1
            ? "?" + string.Join('&', uri.Query[1..].Split('&').Select(p => p.Contains('=') ? $"{p[..p.IndexOf('=')]}={Redacted}" : p))
            : "";
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}{query}";
    }

    // An empty field stays empty: "****" would suggest there was data behind it.
    public static string Mask(string value) =>
        value.Length == 0 ? value
        : value.Length <= 4 ? "****" : value[..2] + new string('*', value.Length - 4) + value[^2..];

    private static string? MaskJson(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return null; }
        if (root is null) return null;
        Walk(root);
        return root.ToJsonString();
    }

    private static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["id"] is JsonValue id && PersonalFieldIds.ContainsKey(id.ToString()) && obj["value"] is JsonValue personal)
                    obj["value"] = Mask(personal.ToString());
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue v)
                    {
                        var kind = Classify(key);
                        if (kind != Kind.None) obj[key] = Apply(kind, v.ToString());
                        else if (FreeText.Contains(key) && v.TryGetValue<string>(out var text))
                            obj[key] = PersonalInText().Replace(text, m => Mask(m.Value));
                        else if (key.Contains("url", StringComparison.OrdinalIgnoreCase) && v.TryGetValue<string>(out var url)) obj[key] = Url(url);
                    }
                    else if (child is not null) Walk(child);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    if (child is not null) Walk(child);
                break;
        }
    }

    // OAuth token requests (Inter) are form-urlencoded: client_secret must not land in clear.
    private static string? MaskForm(string body) =>
        FormBody().IsMatch(body) ? string.Join('&', body.Split('&').Select(MaskPair)) : null;

    private static string MaskPair(string pair)
    {
        var eq = pair.IndexOf('=');
        if (eq < 0) return pair;
        var kind = Classify(Uri.UnescapeDataString(pair[..eq]));
        if (kind == Kind.None) return pair;
        var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        return $"{pair[..eq]}={(kind == Kind.Secret ? Redacted : Uri.EscapeDataString(Mask(value)))}";
    }

    [GeneratedRegex(@"^[\w.%-]+=[^&<>{}]*(&[\w.%-]+=[^&<>{}]*)*$")]
    private static partial Regex FormBody();

    // ponytail: regex over XML elements (NFS-e SP is XML) and over the "key":"value" pairs of a body
    // that is not valid JSON (a malformed fault, a broken app); a full parser only if a format escapes it.
    [GeneratedRegex(@"<(?<tag>[\w:.-]+)>(?<value>[^<]*)</\k<tag>>")]
    private static partial Regex XmlField();

    [GeneratedRegex("\"(?<key>[^\"\\\\]+)\"\\s*:\\s*\"(?<value>[^\"\\\\]*)\"")]
    private static partial Regex LooseJsonField();

    private static string MaskLoose(string body)
    {
        var xml = XmlField().Replace(body, m => $"<{m.Groups["tag"].Value}>{Apply(Classify(m.Groups["tag"].Value), m.Groups["value"].Value)}</{m.Groups["tag"].Value}>");
        return LooseJsonField().Replace(xml, m => $"\"{m.Groups["key"].Value}\":\"{Apply(Classify(m.Groups["key"].Value), m.Groups["value"].Value)}\"");
    }
}
