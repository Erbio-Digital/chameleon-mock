using Microsoft.AspNetCore.Routing.Patterns;
using Chameleon.Sdk;
using Chameleon.Engine;
using Chameleon.Host;
using Chameleon.Host.Plugins;

namespace Chameleon.Host.Http;

/// Marks every endpoint a connector maps, so capture and fault injection apply to it generically.
public sealed record ConnectorEndpoint(string Connector);

public static class ChameleonEndpoints
{
    public const string ControlPrefix = "/_chameleon";
    // Below typical gateway timeouts (60 s), so a latency fault never turns into a gateway timeout.
    public const int MaxFaultDelayMs = 30_000;
    public const double MaxAdvanceSeconds = 366 * 24 * 3600;

    /// "METHOD pattern" as the connector mapped it, e.g. "PUT /bradesco/v2/cob/{txid}". This is the
    /// name a test uses to arm a fault on the route.
    public static string? RouteName(Endpoint? endpoint) =>
        endpoint is RouteEndpoint route && endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { } methods
            ? $"{methods.HttpMethods.FirstOrDefault()} {route.RoutePattern.RawText}"
            : null;

    public static bool IsControl(RoutePattern pattern) =>
        pattern.RawText?.StartsWith(ControlPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static IEndpointRouteBuilder MapChameleon(this IEndpointRouteBuilder routes)
    {
        routes.MapSandboxes();

        // Everything below runs in the current sandbox (/s/{id} prefix, header or "default").
        routes.MapGet($"{ControlPrefix}/journal",
            (IInteractionStore journal, SandboxContext sandbox, string? entityId, string? connector, string? correlationId, InteractionKind? kind) =>
                Results.Ok(journal.Find(new InteractionQuery(sandbox.Id, kind, connector, entityId, correlationId))));

        // Feed page: newest first (nextBeforeSeq pages back); with afterSeq, oldest first (resume a paused feed).
        routes.MapGet($"{ControlPrefix}/interactions", (IInteractionStore journal, SandboxContext sandbox,
            InteractionKind? kind, string? connector, string? entityId, string? correlationId, int? status, int? statusClass, bool? byClock,
            DateTimeOffset? from, DateTimeOffset? to, long? afterSeq, long? beforeSeq, int? limit) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var items = journal.Find(new InteractionQuery(sandbox.Id, kind, connector, entityId, correlationId, null, status,
                from, to, null, afterSeq, beforeSeq, take, statusClass, byClock));
            return Results.Ok(new { items, nextBeforeSeq = afterSeq is null && items.Count == take ? items[^1].Seq : (long?)null });
        });

        routes.MapGet($"{ControlPrefix}/interactions/{{id}}", (string id, IInteractionStore journal, SandboxContext sandbox) =>
            journal.Find(new InteractionQuery(sandbox.Id, Id: id)) is [var found]
                ? Results.Ok(found)
                : Results.NotFound(new { message = "call not found", id }));

        routes.MapGet($"{ControlPrefix}/faults", (IFaultStore faults, SandboxContext sandbox, SandboxClock clock) =>
            Results.Ok(faults.List(sandbox.Id).Where(f => f.Fault.ActiveAt(clock.Now)).Select(f => new
            {
                f.Connector, f.Route, kind = f.Fault.Kind.ToString(), f.Fault.Status, f.Fault.DelayMs,
                remaining = f.Fault.Until is null ? f.Fault.Times : (int?)null, until = f.Fault.Until,
            })));

        routes.MapEntities();

        routes.MapPost($"{ControlPrefix}/verify", (IInteractionStore journal, SandboxContext sandbox, InteractionQuery query) =>
        {
            var matches = journal.Find(query with { SandboxId = sandbox.Id });
            return Results.Ok(new VerifyResult(matches.Count, matches));
        });

        routes.MapPost($"{ControlPrefix}/clock/advance",
            async (SandboxClock clock, RequestScope scope, HttpContext http, ClockAdvanceRequest req) =>
            {
                if (!double.IsFinite(req.Seconds) || Math.Abs(req.Seconds) > MaxAdvanceSeconds)
                    return Results.BadRequest(new { message = $"seconds entre -{MaxAdvanceSeconds} e {MaxAdvanceSeconds}" });
                clock.Advance(TimeSpan.FromSeconds(req.Seconds));
                // Going back undoes nothing and makes nothing due: state guards keep what already ran from running again.
                if (req.Seconds >= 0) await ClockTick.RunAsync(http.RequestServices);
                try
                {
                    scope.Commit();
                }
                catch (ConcurrencyConflictException conflict)
                {
                    scope.Discard();
                    return Results.Conflict(new { message = conflict.Message });
                }
                return Results.Ok(new { now = clock.Now });
            });

        routes.MapGet($"{ControlPrefix}/connectors", (IEnumerable<IConnector> connectors, EndpointDataSource endpoints,
                [Microsoft.AspNetCore.Mvc.FromServices] IReadOnlyList<LoadedPackage> plugins) =>
            Results.Ok(connectors.Select(c =>
            {
                var d = c.Describe();
                // The package of a plugin connector; null for one registered in process (tests).
                var package = plugins.FirstOrDefault(p => p.Assembly == c.GetType().Assembly);
                return new
                {
                    d.Name,
                    d.Entities,
                    d.Actions,
                    d.Webhooks,
                    d.Registrations,
                    d.Title,
                    d.Category,
                    d.Description,
                    d.Credentials,
                    d.Settings,
                    d.HasTestEvent,
                    d.HasAccounts,
                    d.ClientCertificate,
                    d.WebhookCertificate,
                    d.Settlement,
                    routes = ProviderRoutes(endpoints, d.Name),
                    packageId = package?.PackageId,
                    version = package?.Version,
                    verified = package?.Verified ?? false,
                };
            })));

        // The Chameleon CA: what the app trusts in place of the real provider's.
        routes.MapGet($"{ControlPrefix}/ca.crt", (CertificateAuthority ca) => Results.Text(ca.CertificatePem, "application/x-pem-file"));

        // Optional "logo.png" embedded in the connector assembly; the panel falls back to a generic icon.
        routes.MapGet($"{ControlPrefix}/connectors/{{name}}/logo", (string name, IEnumerable<IConnector> connectors) =>
            connectors.FirstOrDefault(c => c.Name == name)?.GetType().Assembly.GetManifestResourceStream("logo.png") is { } logo
                ? Results.Stream(logo, "image/png")
                : Results.NotFound());

        // Webhook registrations of every connector that keeps them. Read outside the connector
        // group so a panel refresh is not journaled; set and remove below are control actions.
        routes.MapGet($"{ControlPrefix}/webhook-registrations", (IEnumerable<IConnector> connectors, RequestScope scope, string? connector) =>
            Results.Ok(connectors.Where(c => connector is null || c.Name == connector)
                .SelectMany(c => c.Describe().Registrations is { } r
                    ? r.List(new ConnectorStore(scope)).Select(w => new { connector = c.Name, w.Key, Url = PayloadSanitizer.Url(w.Url), w.CreatedAt })
                    : [])
                .OrderByDescending(w => w.CreatedAt)));

        routes.MapPost($"{ControlPrefix}/faults", (IFaultStore faults, SandboxContext sandbox, EndpointDataSource endpoints,
            ILiveEventBus live, SandboxClock clock, IEnumerable<IConnector> connectors, ArmFaultRequest req) =>
        {
            if (req.DurationSeconds is not null && req.Times is not null)
                return Results.BadRequest(new { message = "use times or durationSeconds, not both" });
            if (req.DurationSeconds is < 1 or > 86400)
                return Results.BadRequest(new { message = "durationSeconds entre 1 e 86400" });
            if (req.Status is < 400 or > 599) return Results.BadRequest(new { message = "status entre 400 e 599" });
            if (req.DelayMs is < 0 or > MaxFaultDelayMs) return Results.BadRequest(new { message = $"delayMs entre 0 e {MaxFaultDelayMs}" });
            if (req.Times is < 1 or > 1000) return Results.BadRequest(new { message = "times entre 1 e 1000" });
            var known = ProviderRoutes(endpoints, req.Connector);
            if (req.Route == IFaultStore.SettlementRoute)
            {
                if (connectors.FirstOrDefault(c => c.Name == req.Connector)?.Describe().Settlement != true)
                    return Results.BadRequest(new { message = $"{req.Connector} has no asynchronous settlement" });
                if (req.Kind != FaultKind.Hold || req.Times is not null)
                    return Results.BadRequest(new { message = "settlement accepts Hold only, until disarmed or for durationSeconds" });
            }
            else if (req.Route == IFaultStore.WebhookRoute)
            {
                if (req.Kind is not (FaultKind.Latency or FaultKind.Duplicate or FaultKind.Reorder or FaultKind.Hold))
                    return Results.BadRequest(new { message = "webhook delivery accepts Latency, Duplicate, Reorder or Hold" });
                if (req.Kind == FaultKind.Hold && req.Times is not null)
                    return Results.BadRequest(new { message = "Hold does not count uses: it lasts until disarmed, or use durationSeconds" });
                if (known.Count == 0)
                    return Results.BadRequest(new { message = $"conector desconhecido: {req.Connector}" });
            }
            else
            {
                if (req.Route == IFaultStore.AllRoutes && known.Count == 0)
                    return Results.BadRequest(new { message = $"conector desconhecido: {req.Connector}" });
                if (req.Route != IFaultStore.AllRoutes && !known.Contains(req.Route))
                    return Results.BadRequest(new { message = $"unknown route for {req.Connector}",
                        routes = known.Append(IFaultStore.AllRoutes).Append(IFaultStore.WebhookRoute) });
                if (req.Kind is FaultKind.Duplicate or FaultKind.Reorder or FaultKind.Hold)
                    return Results.BadRequest(new { message = $"{req.Kind} applies only to the {IFaultStore.WebhookRoute} route"
                        + (req.Kind == FaultKind.Hold ? $" or {IFaultStore.SettlementRoute}" : "") });
            }

            // Timed by the sandbox clock, so a frozen sandbox ends it only when the clock advances.
            var until = req.DurationSeconds is { } seconds ? clock.Now.AddSeconds(seconds) : (DateTimeOffset?)null;
            var fault = new Fault(req.Kind, req.Status, req.DelayMs, until is null ? req.Times ?? 1 : 0, until);
            faults.Arm(sandbox.Id, req.Connector, req.Route, fault);
            live.Publish(sandbox.Id, [new LiveEvent(LiveEvent.FaultChanged, LiveEvents.Fault(req.Connector, req.Route, req.Kind, until is null ? fault.Times : null))]);
            return Results.Ok(new { armed = new { req.Connector, req.Route, req.Kind, until } });
        });

        routes.MapDelete($"{ControlPrefix}/faults", (IFaultStore faults, SandboxContext sandbox, ILiveEventBus live, string connector, string route) =>
        {
            faults.Clear(sandbox.Id, connector, route);
            live.Publish(sandbox.Id, [new LiveEvent(LiveEvent.FaultChanged, new { connector, route, remaining = 0 })]);
            return Results.NoContent();
        });

        routes.MapWebhooks();
        routes.MapSubscriptions();
        routes.MapAccounts();

        foreach (var connector in routes.ServiceProvider.GetServices<IConnector>())
        {
            var group = routes.MapGroup("").WithMetadata(new ConnectorEndpoint(connector.Name));
            group.AddEndpointFilter(ConnectorFilter);
            connector.MapRoutes(group);

            // Generic actions: any action the connector describes, addressed by entity type and key.
            group.MapPost($"{ControlPrefix}/connectors/{connector.Name}/entities/{{type}}/{{key}}/actions/{{action}}",
                async (string type, string key, string action, HttpContext http, ConnectorContext ctx) =>
                {
                    http.Annotate(entityId: key);
                    var found = connector.Describe().Actions.FirstOrDefault(a => a.EntityType == type && a.Name == action);
                    if (found is null)
                        return Results.NotFound(new { message = $"unknown action {action} for {type} in {connector.Name}" });
                    return await found.Run(ctx, key, await http.Request.ReadActionArgsAsync());
                });

            group.MapSubscriptionChanges(connector);
            if (connector.Describe().HasAccounts) group.MapAccountChanges(connector);

            if (connector.Describe().Registrations is { } registrations)
            {
                var path = $"{ControlPrefix}/connectors/{connector.Name}/webhook-registrations/{{key}}";
                group.MapPut(path, (string key, WebhookRegistrationRequest req, HttpContext http, ConnectorContext ctx) =>
                {
                    http.Annotate(entityId: key);
                    if (!WebhookRegistrations.IsValidUrl(req.Url))
                        return Results.BadRequest(new { message = "invalid url: use an absolute http or https URL" });
                    registrations.Set(ctx, key, req.Url);
                    return Results.Ok(new { connector = connector.Name, key, url = PayloadSanitizer.Url(req.Url), createdAt = ctx.Now });
                });
                group.MapDelete(path, (string key, HttpContext http, ConnectorContext ctx) =>
                {
                    http.Annotate(entityId: key);
                    return registrations.Remove(ctx, key)
                        ? Results.NoContent()
                        : Results.NotFound(new { message = $"no webhook registered for {registrations.Key} {key}" });
                });
            }
        }

        return routes;
    }

    /// Provider-surface routes of a connector (the ones faults can target), in "METHOD pattern" form.
    public static List<string> ProviderRoutes(EndpointDataSource endpoints, string connector) =>
        endpoints.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ConnectorEndpoint>()?.Connector == connector && !IsControl(e.RoutePattern))
            .Select(e => RouteName(e)!)
            .Where(n => n is not null)
            .Distinct()
            .ToList();

    /// Runs around every connector endpoint: tells the context which connector is serving, applies
    /// an armed fault on provider routes, and turns a lifecycle conflict into 409 with the
    /// request's writes discarded.
    private static async ValueTask<object?> ConnectorFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var endpoint = (RouteEndpoint)http.GetEndpoint()!;
        var connector = endpoint.Metadata.GetMetadata<ConnectorEndpoint>()!.Connector;
        var context = http.RequestServices.GetRequiredService<ConnectorContext>();
        context.Connector = connector;

        try
        {
            if (IsControl(endpoint.RoutePattern)) return await next(ctx);
            // Outside "default" the provider answers only where it is subscribed; checked before any armed fault.
            if (context.SandboxId != Sandbox.Default && context.Subscription is null)
                return Results.NotFound(new { message = SubscriptionEndpoints.NotSubscribed });
            // The certificate the app presents must be this sandbox's. None at all is refused like the
            // real provider's failed handshake: connection dropped, no answer, nothing on record.
            if (context.SandboxId != Sandbox.Default && context.Descriptor.ClientCertificate)
            {
                if (http.Connection.ClientCertificate is not { } presented)
                {
                    http.Items[CaptureMiddleware.RefusedConnectionKey] = true;
                    http.Abort();
                    return Results.Empty;
                }
                if (!context.Subscription!.Holds(CertificateAuthority.Thumbprint(presented)))
                    return Results.Json(new { message = SubscriptionEndpoints.ForeignCertificate }, statusCode: StatusCodes.Status403Forbidden);
            }
            return await WithFault(http, connector, endpoint, ctx, next);
        }
        catch (TransitionConflictException conflict)
        {
            http.RequestServices.GetRequiredService<RequestScope>().Discard();
            return Results.Conflict(new { message = conflict.Message });
        }
    }

    private static async ValueTask<object?> WithFault(HttpContext http, string connector, RouteEndpoint endpoint,
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var faults = http.RequestServices.GetRequiredService<IFaultStore>();
        var sandbox = http.RequestServices.GetRequiredService<SandboxContext>();
        var now = http.RequestServices.GetRequiredService<SandboxClock>().Now;
        // The exact route first: a connector-wide fault only fires where none is armed (or left).
        var route = RouteName(endpoint)!;
        if (!faults.TryConsume(sandbox.Id, connector, route, now, out var fault))
        {
            route = IFaultStore.AllRoutes;
            if (!faults.TryConsume(sandbox.Id, connector, route, now, out fault)) return await next(ctx);
        }

        http.Items[CaptureMiddleware.FaultKey] = fault.Kind.ToString();
        http.RequestServices.GetRequiredService<ILiveEventBus>().Publish(sandbox.Id,
            [new LiveEvent(LiveEvent.FaultChanged, LiveEvents.Fault(connector, route, fault.Kind, fault.RemainingAfterUse))]);
        return fault.Kind switch
        {
            // Latency: the real handler still answers, only later (the app's slow path).
            FaultKind.Latency => await Delayed(fault.DelayMs ?? 1000, ctx, next),
            FaultKind.Timeout => Results.StatusCode(504),
            FaultKind.Malformed => Results.Text("{ this is not valid json ", "application/json"),
            _ => Results.StatusCode(fault.Status ?? 500),
        };
    }

    private static async ValueTask<object?> Delayed(int ms, EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        await Task.Delay(ms, ctx.HttpContext.RequestAborted);
        return await next(ctx);
    }
}

public sealed record WebhookRegistrationRequest(string Url);
