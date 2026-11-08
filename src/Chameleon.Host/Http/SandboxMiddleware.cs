using Chameleon.Engine;
using Chameleon.Host.Auth;

namespace Chameleon.Host.Http;

/// Runs before routing: picks the sandbox from the "/s/{id}" prefix (stripped, so connectors map
/// routes without knowing about sandboxes), else the X-Chameleon-Sandbox header, else "default".
/// Unknown or expired sandbox -> 404; an expired one is removed with everything it holds.
public sealed class SandboxMiddleware(RequestDelegate next)
{
    public const string Header = "X-Chameleon-Sandbox";
    private const string Prefix = "/s/";

    public async Task InvokeAsync(HttpContext http, SandboxContext context, ISandboxStore sandboxes)
    {
        var id = FromPath(http) ?? NullIfEmpty(http.Request.Headers[Header].ToString()) ?? Sandbox.Default;

        var sandbox = Sandbox.IsValidId(id) ? sandboxes.Get(id) : null;
        if (sandbox is not null && sandbox.IsExpired(DateTimeOffset.UtcNow))
        {
            Remove(sandbox.Id, http.RequestServices);
            sandbox = null;
        }
        // On the shared instance "default" accepts any credential, so its provider routes
        // answer as if it did not exist; the control API and the OAuth resource metadata still run there.
        if (sandbox?.Id == Sandbox.Default && ChameleonAuth.IsEnabled(http.RequestServices.GetRequiredService<IConfiguration>())
            && !http.Request.Path.StartsWithSegments(ChameleonEndpoints.ControlPrefix)
            && !http.Request.Path.StartsWithSegments(ChameleonAuth.ResourceMetadataPath))
            sandbox = null;
        if (sandbox is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { message = "sandbox not found", sandbox = id });
            return;
        }

        context.Use(sandbox);
        http.Response.Headers[Header] = sandbox.Id;
        await next(http);
    }

    /// Removes the sandbox with everything it holds.
    public static void Remove(string id, IServiceProvider services)
    {
        Clear(id, services);
        services.GetRequiredService<ISandboxStore>().Delete(id);
        services.GetRequiredService<ILiveEventBus>().Publish(id, [new LiveEvent(LiveEvent.SandboxChanged, new { id, removed = true })]);
    }

    /// Empties the sandbox: entities, journal, faults and webhook deliveries.
    public static void Clear(string id, IServiceProvider services)
    {
        services.GetRequiredService<IEntityStore>().DeleteSandbox(id);
        services.GetRequiredService<IInteractionStore>().DeleteSandbox(id);
        services.GetRequiredService<IFaultStore>().DeleteSandbox(id);
        services.GetRequiredService<IWebhookOutbox>().DeleteSandbox(id);
    }

    private static string? FromPath(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        http.Request.PathBase = http.Request.PathBase.Add(Prefix + id);
        http.Request.Path = slash < 0 ? "/" : rest[slash..];
        return id;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
