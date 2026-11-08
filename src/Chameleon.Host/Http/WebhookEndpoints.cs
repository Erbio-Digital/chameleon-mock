using Chameleon.Engine;

namespace Chameleon.Host.Http;

/// Webhook deliveries of the current sandbox: list, detail with attempts, manual resend.
public static class WebhookEndpoints
{
    private const string Base = $"{ChameleonEndpoints.ControlPrefix}/webhooks";

    public static void MapWebhooks(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Base, (IWebhookOutbox outbox, SandboxContext sandbox, string? entityId, DeliveryStatus? status) =>
            Results.Ok(outbox.Find(sandbox.Id, entityId, status).Select(View)));

        routes.MapGet($"{Base}/{{id}}", (string id, IWebhookOutbox outbox, SandboxContext sandbox) =>
            outbox.Get(sandbox.Id, id) is { } d
                ? Results.Ok(new { delivery = View(d), payload = PayloadSanitizer.Body(d.Payload).Body, attempts = outbox.Attempts(d.Id).Select(a => a with { Url = PayloadSanitizer.Url(a.Url) }) })
                : NotFound(id));

        routes.MapPost($"{Base}/{{id}}/resend", (string id, ResendWebhookRequest? req, IWebhookOutbox outbox,
            SandboxContext sandbox, WebhookDestinationPolicy policy, IWebhookSignal signal) =>
        {
            if (outbox.Get(sandbox.Id, id) is null) return NotFound(id);
            if (req?.Url is { } url && policy.Refuse(url) is { } refused)
                return Results.BadRequest(new { message = refused });
            if (!outbox.Resend(sandbox.Id, id, req?.Url, DateTimeOffset.UtcNow))
                return Results.Conflict(new { message = "delivery still in progress" });
            signal.Signal(outbox.Get(sandbox.Id, id)!);
            return Results.Accepted($"{Base}/{id}");
        });
    }

    private static IResult NotFound(string id) => Results.NotFound(new { message = "delivery not found", id });

    private static object View(WebhookDelivery d) => LiveEvents.Webhook(d);
}
