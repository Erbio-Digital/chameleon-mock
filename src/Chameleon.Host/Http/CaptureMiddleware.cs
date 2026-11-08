using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Host.Http;

/// Wraps every request: assigns the correlation id, records each connector call (request,
/// response, status, duration) with sensitive data sanitized, and commits the request scope
/// only when the request completes without throwing.
public sealed partial class CaptureMiddleware(RequestDelegate next)
{
    public const string FaultKey = "chameleon.fault";
    /// Set when the connection was refused like a failed TLS handshake: nothing goes on record.
    public const string RefusedConnectionKey = "chameleon.refused-connection";
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext http, RequestScope scope, SandboxClock clock, IEnumerable<IConnector> connectors)
    {
        var incoming = http.Request.Headers[CorrelationHeader].ToString();
        scope.CorrelationId = SafeCorrelation().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        http.Response.Headers[CorrelationHeader] = scope.CorrelationId;

        var endpoint = http.GetEndpoint() as RouteEndpoint;
        // Routing answers 404, 405 and 415 itself, with no connector endpoint: the call still belongs to
        // the connector its path names, and the app's mistake is what a tester most needs to see.
        var connector = endpoint?.Metadata.GetMetadata<ConnectorEndpoint>()?.Connector;
        if (connector is null)
        {
            endpoint = null;
            connector = ConnectorOfPath(http.Request.Path, connectors);
        }
        if (connector is null)
        {
            await next(http);
            scope.Commit();
            return;
        }

        var at = clock.Now;
        var watch = Stopwatch.StartNew();
        http.Request.EnableBuffering();
        string requestBody;
        if (PayloadSanitizer.IsText(http.Request.ContentType))
            requestBody = await ReadAsync(http.Request.Body);
        else
        {
            await http.Request.Body.CopyToAsync(Stream.Null, http.RequestAborted);
            requestBody = PayloadSanitizer.Binary(http.Request.Body.Position);
        }
        http.Request.Body.Position = 0;

        var originalBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        Exception? failure = null;
        try
        {
            await next(http);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            http.Response.Body = originalBody;
        }

        if (http.Items.ContainsKey(RefusedConnectionKey))
        {
            scope.Discard();
            return;
        }

        string? responseBody = null;
        if (failure is null)
        {
            buffer.Position = 0;
            responseBody = PayloadSanitizer.IsText(http.Response.ContentType) ? await ReadAsync(buffer) : PayloadSanitizer.Binary(buffer.Length);
        }
        else
        {
            // The handler's writes never happened; the call itself still goes on record.
            scope.Discard();
        }

        var fault = http.Items[FaultKey] as string;
        var note = http.Items[ChameleonHttp.NoteKey] as string;
        var path = http.Request.Path.Value ?? "";
        Interaction Call(int status, string? body, string? detail)
        {
            var request = PayloadSanitizer.Body(requestBody);
            var response = PayloadSanitizer.Body(body);
            return new Interaction(
                endpoint is not null && ChameleonEndpoints.IsControl(endpoint.RoutePattern) ? InteractionKind.ControlAction : InteractionKind.InboundRequest,
                connector,
                http.Items[ChameleonHttp.EntityIdKey] as string ?? (endpoint is null ? null : SingleRouteValue(http, endpoint)),
                at,
                $"{http.Request.Method} {path}" + (fault is null ? "" : $" [fault:{fault}]"),
                detail ?? (endpoint is null ? "refused by routing: a route, method or content-type the provider does not serve" : null))
            {
                Method = http.Request.Method,
                Path = path + PayloadSanitizer.Query(http.Request.QueryString.Value),
                StatusCode = status,
                DurationMs = watch.ElapsedMilliseconds,
                RequestHeaders = PayloadSanitizer.Headers(http.Request.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToString()))),
                RequestBody = request.Body,
                ResponseBody = response.Body,
                Truncated = request.Truncated || response.Truncated,
                Fault = fault,
            };
        }

        scope.RecordCause(Call(
            failure is null ? http.Response.StatusCode : StatusCodes.Status500InternalServerError,
            responseBody,
            failure is null ? note : $"unhandled exception: {failure.GetType().Name}"));
        try
        {
            scope.Commit();
        }
        catch (ConcurrencyConflictException conflict) when (failure is null)
        {
            // Another operation changed the same entity first: nothing of this request
            // lands and the app gets a conflict instead of the answer it was about to receive.
            scope.Discard();
            responseBody = JsonSerializer.Serialize(new { message = conflict.Message });
            http.Response.ContentLength = null;
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            http.Response.ContentType = "application/json";
            buffer.SetLength(0);
            buffer.Write(Encoding.UTF8.GetBytes(responseBody));
            scope.RecordCause(Call(StatusCodes.Status409Conflict, responseBody, "version conflict"));
            scope.Commit();
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, http.RequestAborted);
    }

    /// "/bradesco/..." (or the legacy "/_chameleon/bradesco/...") names the connector; anything else is not a provider call.
    private static string? ConnectorOfPath(PathString path, IEnumerable<IConnector> connectors)
    {
        var segments = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments is ["_chameleon", var legacy, ..] ? legacy : segments.FirstOrDefault();
        return connectors.FirstOrDefault(c => c.Name == first)?.Name;
    }

    private static string? SingleRouteValue(HttpContext http, RouteEndpoint endpoint) =>
        endpoint.RoutePattern.Parameters is [var only] ? http.Request.RouteValues[only.Name]?.ToString() : null;

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeCorrelation();
}
