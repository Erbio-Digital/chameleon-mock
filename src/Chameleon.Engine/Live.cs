using System.Text.Json;

namespace Chameleon.Engine;

/// Something the panel shows live: interaction, entityChanged, webhookUpdated, faultChanged,
/// sandboxChanged or overflow. Data is already safe to show (masked like the journal).
public sealed record LiveEvent(string Type, object Data)
{
    public const string Interaction = "interaction";
    public const string EntityChanged = "entityChanged";
    public const string WebhookUpdated = "webhookUpdated";
    public const string FaultChanged = "faultChanged";
    public const string SandboxChanged = "sandboxChanged";
    public const string Overflow = "overflow";
}

/// Pushes events to whoever watches a sandbox (SignalR in the Host).
/// Called only after the change is committed.
public interface ILiveEventBus
{
    void Publish(string sandboxId, IReadOnlyCollection<LiveEvent> events);
}

public static class LiveEvents
{
    public static object Webhook(WebhookDelivery d) => new
    {
        d.Id,
        d.SandboxId,
        d.Connector,
        d.EntityId,
        d.CorrelationId,
        Url = PayloadSanitizer.Url(d.Url),
        status = d.Status.ToString(),
        @event = EventOf(d.Payload),
        d.Attempts,
        d.MaxAttempts,
        d.BaseDelayMs,
        d.TimeoutMs,
        d.NextAttemptAt,
        d.Note,
        d.CreatedAt,
    };

    public static object Sandbox(Sandbox s, DateTimeOffset utcNow) => new
    {
        s.Id,
        s.Persistent,
        clockMode = s.ClockMode.ToString(),
        now = s.Now(utcNow),
        // Positive: the sandbox clock is ahead of real time.
        driftSeconds = Math.Round((s.Now(utcNow) - utcNow).TotalSeconds),
        s.CreatedAt,
        s.ExpiresAt,
        s.WebhookPolicy,
        // Values never leave the server: the panel, the MCP and the live channel see only where they go.
        webhookHeaders = s.WebhookHeaders.Select(h => new { h.Host, h.Name }),
    };

    /// What the delivery announces: the payload's "evento" (NFS-e), else its root key ("pix").
    public static string? EventOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("evento", out var evento) && evento.ValueKind == JsonValueKind.String)
                return evento.GetString();
            return doc.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static object Fault(string connector, string route, FaultKind kind, int? remaining) =>
        new { connector, route, kind = kind.ToString(), remaining };
}
