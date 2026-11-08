using Chameleon.Engine;
using Chameleon.Host.Auth;

namespace Chameleon.Host.Http;

/// Sandbox lifecycle. These routes address a sandbox by id in the path, not by the
/// current-sandbox resolution the rest of the control API uses.
public static class SandboxEndpoints
{
    public const int DefaultTtlSeconds = 24 * 60 * 60;
    private const string Base = $"{ChameleonEndpoints.ControlPrefix}/sandboxes";

    /// "default" is the only sandbox that exists from the start: it keeps the frozen-clock behavior for local runs and tests.
    public static void Seed(ISandboxStore sandboxes)
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (sandboxes.Get(Sandbox.Default) is null)
            sandboxes.Put(new Sandbox { Id = Sandbox.Default, ClockMode = ClockMode.Frozen, FrozenAt = utcNow });
    }

    public static void MapSandboxes(this IEndpointRouteBuilder routes)
    {
        routes.MapWebhookPolicy();

        routes.MapGet(Base, (ISandboxStore sandboxes, ILiveEventBus live) =>
            Results.Ok(sandboxes.All().Select(View)));

        routes.MapGet($"{Base}/{{id}}", (string id, ISandboxStore sandboxes, ILiveEventBus live) =>
            sandboxes.Get(id) is { } s ? Results.Ok(View(s)) : NotFound(id));

        routes.MapPost(Base, (CreateSandboxRequest req, ISandboxStore sandboxes, ILiveEventBus live, HttpContext http) =>
        {
            if (!Sandbox.IsValidId(req.Id))
                return Results.BadRequest(new { message = "invalid id: [a-z0-9-], up to 40 characters, starting with a letter or digit" });
            if (req.TtlSeconds is <= 0)
                return Results.BadRequest(new { message = "ttlSeconds must be positive" });
            if (req.Permanent && req.TtlSeconds is not null)
                return Results.BadRequest(new { message = "a permanent sandbox does not expire: do not send ttlSeconds" });
            if (sandboxes.Get(req.Id) is not null)
                return Results.Conflict(new { message = $"sandbox {req.Id} already exists" });
            var source = req.From is null ? null : sandboxes.Get(req.From);
            if (req.From is not null && (source is null || source.IsExpired(DateTimeOffset.UtcNow)))
                return Results.NotFound(new { message = "source not found", sandbox = req.From });

            var utcNow = DateTimeOffset.UtcNow;
            var mode = source?.ClockMode ?? req.ClockMode ?? ClockMode.Frozen;
            var sandbox = new Sandbox
            {
                Id = req.Id,
                Persistent = req.Permanent,
                ClockMode = mode,
                // A copy starts at the source's instant, frozen or running from there.
                FrozenAt = mode == ClockMode.Frozen ? source?.Now(utcNow) ?? utcNow : null,
                ClockOffsetSeconds = mode == ClockMode.Realtime ? source?.ClockOffsetSeconds ?? 0 : 0,
                WebhookPolicy = source?.WebhookPolicy,
                WebhookHeaders = source?.WebhookHeaders.ToList() ?? [],
                CreatedAt = utcNow,
                ExpiresAt = req.Permanent ? null : utcNow.AddSeconds(req.TtlSeconds ?? DefaultTtlSeconds),
            };
            sandboxes.Put(sandbox);
            if (source is not null) CopyContents(source.Id, sandbox.Id, http.RequestServices);
            Changed(live, sandbox);
            return Results.Created($"{Base}/{sandbox.Id}", View(sandbox));
        });

        routes.MapPost($"{Base}/{{id}}/reset", (string id, string? confirm, ISandboxStore sandboxes, ILiveEventBus live, HttpContext http) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            // Only a logged operator resets a permanent sandbox.
            if (stored.Persistent && ChameleonAuth.Require(http, ChameleonAuth.Operator) is { } denied) return denied;
            if (stored.Persistent && confirm != id)
                return Results.Conflict(new { message = $"sandbox {id} is permanent: confirm with ?confirm={id}" });

            SandboxMiddleware.Clear(id, http.RequestServices);
            var sandbox = stored.Copy();
            sandbox.ResetClock(DateTimeOffset.UtcNow);
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });

        // default never goes away; another permanent one goes only with ?confirm={id}, like reset.
        routes.MapDelete($"{Base}/{{id}}", (string id, string? confirm, ISandboxStore sandboxes, ILiveEventBus live, HttpContext http) =>
        {
            if (sandboxes.Get(id) is not { } sandbox) return NotFound(id);
            if (sandbox.Id == Sandbox.Default)
                return Results.Conflict(new { message = $"sandbox {id} cannot be removed; use reset" });
            if (sandbox.Persistent && confirm != id)
                return Results.Conflict(new { message = $"sandbox {id} is permanent: confirm with ?confirm={id}" });

            SandboxMiddleware.Remove(id, http.RequestServices);
            return Results.NoContent();
        });

        // "Agora": back to real time, nothing else touched (unlike reset).
        routes.MapPost($"{Base}/{{id}}/clock/now", (string id, ISandboxStore sandboxes, ILiveEventBus live) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            var sandbox = stored.Copy();
            sandbox.ClockMode = ClockMode.Realtime;
            sandbox.ResetClock(DateTimeOffset.UtcNow);
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });

        routes.MapPost($"{Base}/{{id}}/clock", (string id, SetClockModeRequest req, ISandboxStore sandboxes, ILiveEventBus live) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            var sandbox = stored.Copy();
            sandbox.SetMode(req.Mode, DateTimeOffset.UtcNow);
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });
    }

    private static void MapWebhookPolicy(this IEndpointRouteBuilder routes)
    {
        routes.MapPut($"{Base}/{{id}}/webhook-policy", (string id, WebhookPolicy policy, ISandboxStore sandboxes, ILiveEventBus live) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            if (policy.Invalid() is { } invalid) return Results.BadRequest(new { message = invalid });
            var sandbox = stored.Copy();
            sandbox.WebhookPolicy = policy;
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });

        // Replaces the whole list. A header sent without value keeps the one stored for the same host and
        // name, so a client that never read the secret can still edit the list.
        routes.MapPut($"{Base}/{{id}}/webhook-headers", (string id, SetWebhookHeadersRequest req, ISandboxStore sandboxes, ILiveEventBus live) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            var items = req.Headers ?? [];
            if (items.Count > WebhookHeader.Max) return Results.BadRequest(new { message = $"at most {WebhookHeader.Max} headers" });
            var headers = items.Select(h => new WebhookHeader(h.Host?.Trim().ToLowerInvariant() ?? "", h.Name?.Trim() ?? "",
                    h.Value ?? stored.WebhookHeaders.FirstOrDefault(o => o.Host == h.Host?.Trim().ToLowerInvariant()
                        && string.Equals(o.Name, h.Name?.Trim(), StringComparison.OrdinalIgnoreCase))?.Value ?? ""))
                .ToList();
            if (headers.Select(h => h.Invalid()).FirstOrDefault(e => e is not null) is { } invalid) return Results.BadRequest(new { message = invalid });
            if (headers.GroupBy(h => (h.Host, h.Name.ToLowerInvariant())).Any(g => g.Count() > 1))
                return Results.BadRequest(new { message = "repeated header for the same host" });
            var sandbox = stored.Copy();
            sandbox.WebhookHeaders = headers;
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });

        routes.MapDelete($"{Base}/{{id}}/webhook-policy", (string id, ISandboxStore sandboxes, ILiveEventBus live) =>
        {
            if (sandboxes.Get(id) is not { } stored) return NotFound(id);
            var sandbox = stored.Copy();
            sandbox.WebhookPolicy = null;
            sandboxes.Put(sandbox);
            Changed(live, sandbox);
            return Results.Ok(View(sandbox));
        });
    }

    /// Entities and armed faults; journal and deliveries stay with the source.
    private static void CopyContents(string from, string to, IServiceProvider services)
    {
        services.GetRequiredService<IEntityStore>().CopySandbox(from, to);
        var faults = services.GetRequiredService<IFaultStore>();
        foreach (var armed in faults.List(from))
            faults.Arm(to, armed.Connector, armed.Route, armed.Fault);
    }

    private static IResult NotFound(string id) => Results.NotFound(new { message = "sandbox not found", sandbox = id });

    private static object View(Sandbox s) => LiveEvents.Sandbox(s, DateTimeOffset.UtcNow);

    private static void Changed(ILiveEventBus live, Sandbox s) =>
        live.Publish(s.Id, [new LiveEvent(LiveEvent.SandboxChanged, View(s))]);
}

public sealed record SetWebhookHeadersRequest(List<WebhookHeaderInput>? Headers);

/// Value null: keep the stored one.
public sealed record WebhookHeaderInput(string? Host, string? Name, string? Value);
