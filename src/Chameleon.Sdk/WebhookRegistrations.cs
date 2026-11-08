using System.Text.Json.Serialization;
using Chameleon.Engine;

namespace Chameleon.Sdk;

/// Where the app under test asked the provider to notify (e.g. Pix PUT /v2/webhook/{chave}).
public sealed record WebhookRegistration(string Key, string Url, DateTimeOffset CreatedAt);

/// A connector whose provider keeps webhook registrations declares how to read and change them,
/// so the panel, control API and MCP manage them without connector-specific code. In the real
/// provider only its API changes them; here the operator can too, to set up a test faster.
public sealed record WebhookRegistrations(
    // What identifies a registration at the provider, e.g. "chave".
    string Key,
    [property: JsonIgnore] Func<ConnectorStore, IEnumerable<WebhookRegistration>> List,
    [property: JsonIgnore] Action<ConnectorContext, string, string> Set,
    // False when there was nothing to remove.
    [property: JsonIgnore] Func<ConnectorContext, string, bool> Remove)
{
    public static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
