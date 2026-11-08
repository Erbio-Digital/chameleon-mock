using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Zendesk;

/// The signed ticket.status_changed event, the actions (what the agent does in Zendesk)
/// and the default automation that closes solved tickets.
public sealed partial class ZendeskConnector
{
    internal const string WebhookSecretEnv = "WEBHOOK_SECRET";
    private const string StatusEvent = "status_changed";
    private const string InvalidSignatureNote = "invalid signature (action)";
    // Zendesk uses -1 for changes made by the system (automations).
    private const long SystemActor = -1;

    // ponytail: Zendesk retries a few times on network errors, timeouts, 409, 429 and 5xx, without a
    // published schedule; 3 attempts from 10 s doubling, 12 s each, approximates it.
    private static readonly WebhookPolicy Policy = new(MaxAttempts: 3, BaseDelayMs: 10_000, TimeoutMs: 12_000);

    private static readonly WebhookRegistrations Registrations = new("evento",
        store => store.All<ZendeskWebhook>().Where(w => w.Url is not null).Select(w => new WebhookRegistration(w.Evento, w.Url!, w.CreatedAt)),
        (ctx, key, url) => ctx.Save(key, new ZendeskWebhook { Evento = key, Url = url, CreatedAt = ctx.Now }),
        (ctx, key) =>
        {
            if (ctx.Store.Get<ZendeskWebhook>(key) is not { Url: not null } w) return false;
            ctx.Save(key, new ZendeskWebhook { Evento = key, Url = null, CreatedAt = w.CreatedAt });
            return true;
        });

    private static string? StatusWebhookUrl(ConnectorContext ctx) => ctx.Store.Get<ZendeskWebhook>(StatusEvent)?.Url;

    // Lazy: static initializers of partial files run in no guaranteed order, and these need Ticket.
    private static readonly Lazy<ConnectorAction[]> Actions = new(() =>
    [
        MoveAction("start_analysis", "Start analysis (open)", TicketStatus.open),
        MoveAction("request_info", "Ask the requester for information (pending)", TicketStatus.pending, new ActionParameter("comment")),
        MoveAction("forward_to_third_party", "Forward to a third party (hold)", TicketStatus.hold, new ActionParameter("comment")),
        Ticket.Action("resolve", "Resolve (solved), with outcome accepted, partially_accepted or rejected", Resolve,
            new ActionParameter("outcome"), new ActionParameter("amount", "number"), new ActionParameter("comment")),
        MoveAction("reopen", "Reopen (open)", TicketStatus.open),
        MoveAction("close", "Close (closed)", TicketStatus.closed),
        TicketAction("comment", "Comment as the agent", CommentAction, new ActionParameter("text", Required: true), new ActionParameter("public", "boolean")),
        TicketAction("mark_refunded", "Mark refund done (tag)", MarkRefunded),
        TicketAction("resend_invalid_signature", "Resend the last event with an invalid signature", ResendInvalidSignature),
    ]);

    /// Every status move (API, action, clock) goes through here and emits the event.
    internal static async Task ChangeStatus(ConnectorContext ctx, ZendeskTicket ticket, TicketStatus to, string reason, long actor)
    {
        var from = ticket.Status;
        ticket.CloseAt = to == TicketStatus.solved ? ctx.Now.AddDays(SettingOr(ctx, CloseDays, 4)) : null;
        ticket.UpdatedAt = ctx.Now;
        ctx.Move(Ticket, ticket, to, reason);
        var payload = StatusChanged(ctx, ticket, from, actor);
        ticket.LastEvent = payload.ToJsonString();
        await ctx.NotifyAsync(ticket.Key, StatusWebhookUrl(ctx), payload);
    }

    public async Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        foreach (var t in ctx.Due<ZendeskTicket>().Where(t => t.Status == TicketStatus.solved).ToList())
            await ChangeStatus(ctx, t, TicketStatus.closed, "clock", SystemActor);
    }

    /// The event-subscribed webhook body: ids as strings, status in uppercase.
    private static JsonObject StatusChanged(ConnectorContext ctx, ZendeskTicket t, TicketStatus from, long actor) => new()
    {
        ["account_id"] = ZendeskAccount.AccountId,
        ["detail"] = new JsonObject
        {
            ["actor_id"] = actor.ToString(),
            ["assignee_id"] = null,
            ["brand_id"] = t.BrandId?.ToString(),
            ["created_at"] = Ts(t.CreatedAt),
            ["description"] = t.Description,
            ["external_id"] = t.ExternalId,
            ["form_id"] = t.TicketFormId?.ToString(),
            ["group_id"] = null,
            ["id"] = t.Id.ToString(),
            ["is_public"] = true,
            ["organization_id"] = null,
            ["priority"] = null,
            ["requester_id"] = t.RequesterId.ToString(),
            ["status"] = Upper(t.Status),
            ["subject"] = t.Subject,
            ["submitter_id"] = t.SubmitterId.ToString(),
            ["tags"] = new JsonArray(t.Tags.Select(tag => (JsonNode?)tag).ToArray()),
            ["type"] = null,
            ["updated_at"] = Ts(t.UpdatedAt),
            ["via"] = new JsonObject { ["channel"] = "api" },
        },
        ["event"] = new JsonObject { ["current"] = Upper(t.Status), ["previous"] = Upper(from) },
        ["id"] = Guid.NewGuid().ToString(),
        ["subject"] = $"zen:ticket:{t.Id}",
        ["time"] = Ts(ctx.Now),
        ["type"] = "zen:event-type:ticket.status_changed",
        ["zendesk_event_version"] = "2022-11-06",
    };

    private static string Upper(TicketStatus s) => s.ToString().ToUpperInvariant();

    /// x-zendesk-webhook-signature = base64(HMAC-SHA256(secret, timestamp + body)), per attempt.
    private static IReadOnlyDictionary<string, string> Sign(WebhookSigningInput input)
    {
        if (input.Credential(WebhookSecretEnv) is not { } secret) return new Dictionary<string, string>();
        if (input.Note?.Contains(InvalidSignatureNote) == true) secret = "invalid-signature";
        var timestamp = input.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + input.Body));
        return new Dictionary<string, string>
        {
            ["x-zendesk-webhook-signature"] = Convert.ToBase64String(mac),
            ["x-zendesk-webhook-signature-timestamp"] = timestamp,
        };
    }

    private static IReadOnlyList<TestEvent> TestEvent(ConnectorContext ctx)
    {
        if (StatusWebhookUrl(ctx) is not { } url) return [];
        var sample = new ZendeskTicket { Id = TicketBase, Status = TicketStatus.open, Subject = "Test event", CreatedAt = ctx.Now, UpdatedAt = ctx.Now };
        return [new TestEvent(url, StatusChanged(ctx, sample, TicketStatus.@new, SystemActor))];
    }

    // ---- actions ----

    private static ConnectorAction MoveAction(string trigger, string label, TicketStatus to, params ActionParameter[] parameters) =>
        Ticket.Action(trigger, label, async (ctx, t, args) =>
        {
            if (Arg(args, "comment") is { Length: > 0 } text) AddComment(ctx, t, text, null, true, AgentUserId, []);
            await ChangeStatus(ctx, t, to, "action", AgentUserId);
            return Done(t);
        }, parameters);

    /// An action that does not move the status: allowed while the ticket is not closed.
    private static ConnectorAction TicketAction(string name, string label, Func<ConnectorContext, ZendeskTicket, JsonElement?, Task<IResult>> run,
        params ActionParameter[] parameters) =>
        new(name, Ticket.EntityType, label, Enum.GetNames<TicketStatus>().Where(s => s != nameof(TicketStatus.closed)).ToList(), parameters,
            async (ctx, key, args) => ctx.Store.Get<ZendeskTicket>(key) switch
            {
                null => Results.NotFound(new { message = $"ticket {key} not found" }),
                { Status: TicketStatus.closed } => Results.Conflict(new { message = "a closed ticket accepts no change" }),
                var t => await run(ctx, t, args),
            });

    private static async Task<IResult> Resolve(ConnectorContext ctx, ZendeskTicket t, JsonElement? args)
    {
        if (Arg(args, "outcome") is { Length: > 0 } outcome)
        {
            if (outcome is not ("accepted" or "partially_accepted" or "rejected"))
                return Results.BadRequest(new { message = "outcome: accepted, partially_accepted or rejected" });
            ApplyFields(t, [new CustomField(ZendeskAccount.CaseStatus, ZendeskAccount.CaseStatusPrefix + outcome)]);
        }
        if (Arg(args, "amount") is { Length: > 0 } value)
        {
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                return Results.BadRequest(new { message = "amount: a positive number, with a decimal point" });
            ApplyFields(t, [new CustomField(ZendeskAccount.Amount, amount.ToString("F2", CultureInfo.InvariantCulture))]);
        }
        if (Arg(args, "comment") is { Length: > 0 } text) AddComment(ctx, t, text, null, true, AgentUserId, []);
        await ChangeStatus(ctx, t, TicketStatus.solved, "action", AgentUserId);
        return Done(t);
    }

    private static Task<IResult> CommentAction(ConnectorContext ctx, ZendeskTicket t, JsonElement? args)
    {
        if (Arg(args, "text") is not { Length: > 0 } text) return Task.FromResult(Results.BadRequest(new { message = "text is required" }));
        var comment = AddComment(ctx, t, text, null, Arg(args, "public") != "false", AgentUserId, []);
        t.UpdatedAt = ctx.Now;
        ctx.Save(t.Key, t);
        return Task.FromResult(Results.Ok(new { id = t.Id, comment_id = comment.Id }));
    }

    private static Task<IResult> MarkRefunded(ConnectorContext ctx, ZendeskTicket t, JsonElement? _)
    {
        if (!t.Tags.Contains(ZendeskAccount.RefundTag)) t.Tags.Add(ZendeskAccount.RefundTag);
        t.UpdatedAt = ctx.Now;
        ctx.Save(t.Key, t);
        return Task.FromResult(Done(t));
    }

    private static async Task<IResult> ResendInvalidSignature(ConnectorContext ctx, ZendeskTicket t, JsonElement? _)
    {
        if (t.LastEvent is null) return Results.Conflict(new { message = "the ticket has not emitted a status event yet" });
        await ctx.NotifyAsync(t.Key, StatusWebhookUrl(ctx), JsonNode.Parse(t.LastEvent)!, InvalidSignatureNote);
        return Done(t);
    }

    private static IResult Done(ZendeskTicket t) => Results.Ok(new { id = t.Id, status = t.Status.ToString(), tags = t.Tags });

    private static string? Arg(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;
}
