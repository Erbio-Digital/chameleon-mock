using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol;

namespace Chameleon.Host.Mcp;

/// The MCP tools talk to the control API of this same Host, so they get the same validation,
/// sandbox resolution and live events as the panel (and, from phase 7, the same auth: the
/// caller's Authorization header is passed along). An error from the API becomes a tool error
/// with the API's own message.
public sealed class ControlApiClient(HttpClient http, IServer server, IHttpContextAccessor accessor)
{
    public static string Scoped(string sandbox, string path) => $"/s/{Uri.EscapeDataString(sandbox)}/_chameleon{path}";

    public Task<JsonElement> Get(string path) => Send(HttpMethod.Get, path);

    public async Task<JsonElement> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress(), path));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (accessor.HttpContext?.Request.Headers.Authorization.ToString() is { Length: > 0 } auth)
            request.Headers.TryAddWithoutValidation("Authorization", auth);

        // The MCP call's own token: a client that gives up stops the control API call too.
        var ct = accessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = text.Length == 0 ? default : Parse(text);
        if (response.IsSuccessStatusCode) return json;

        var message = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m) ? m.GetString() : text;
        throw new McpException($"{(int)response.StatusCode} {response.ReasonPhrase}: {message}");
    }

    private static JsonElement Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    /// Where this process listens (Kestrel), with wildcard hosts turned into loopback.
    private Uri BaseAddress()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "http://localhost";
        return new Uri(address.Replace("://0.0.0.0", "://127.0.0.1").Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1")
            .Replace("://[::]", "://127.0.0.1"));
    }
}
