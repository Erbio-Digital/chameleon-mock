using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Zendesk;

/// Zendesk Support API v2 as a typical helpdesk integration uses it: tickets, comments, users, search, uploads and the signed
/// ticket.status_changed event. Basic auth "{email}/token:{api_token}".
public sealed partial class ZendeskConnector : IConnector
{
    public const string ConnectorName = "zendesk";

    private const string EmailEnv = "EMAIL";
    private const string TokenEnv = "API_TOKEN";

    // Ids far from those of real tickets an app may already hold.
    private const long TicketBase = 900000, UserBase = 800000, CommentBase = 700000, AttachmentBase = 600000;
    private const long ApiUserId = 790001, AgentUserId = 790002;

    internal static readonly Lifecycle<ZendeskTicket, TicketStatus> Ticket = BuildLifecycle();

    private static Lifecycle<ZendeskTicket, TicketStatus> BuildLifecycle()
    {
        var l = new Lifecycle<ZendeskTicket, TicketStatus>("ticket", TicketStatus.@new, t => t.Key, t => t.Status, (t, s) => t.Status = s);
        TicketStatus[] open = [TicketStatus.@new, TicketStatus.open, TicketStatus.pending, TicketStatus.hold];
        foreach (var from in open.Except([TicketStatus.open])) l.Allow(from, TicketStatus.open, "start_analysis");
        foreach (var from in open.Except([TicketStatus.pending])) l.Allow(from, TicketStatus.pending, "request_info");
        foreach (var from in open.Except([TicketStatus.hold])) l.Allow(from, TicketStatus.hold, "forward_to_third_party");
        foreach (var from in open) l.Allow(from, TicketStatus.solved, "resolve");
        return l.Allow(TicketStatus.solved, TicketStatus.open, "reopen")
            .Allow(TicketStatus.solved, TicketStatus.closed, "close")
            .Allow(TicketStatus.solved, TicketStatus.closed, "clock");
    }

    private static readonly ConnectorSetting CloseDays = new("DAYS_TO_CLOSE", "Days to close a solved ticket", "number",
        Help: "Default Zendesk automation: solved becomes closed after this delay, on the sandbox clock. Default 4.");
    private static readonly ConnectorSetting IndexDelay = new("SEARCH_INDEX_DELAY_SECONDS", "Search indexing delay (seconds)", "number",
        Help: "A new ticket shows in search only after this delay, on the sandbox clock. Default 0.");

    private static readonly JsonSerializerOptions Json = new();

    public ZendeskConnector() => PayloadSanitizer.MaskFieldIds(ZendeskAccount.Personal);

    public string Name => ConnectorName;

    public ConnectorDescriptor Describe() => new(Name, [Ticket.Describe("id")], Actions.Value, Policy, Registrations)
    {
        Title = "Zendesk",
        Category = "helpdesk",
        Description = "Tickets, comments, search, attachments and a signed status change webhook.",
        Credentials =
        [
            new(EmailEnv, "Agent e-mail", CredentialKind.Id),
            new(TokenEnv, "API token", CredentialKind.Secret),
            new(WebhookSecretEnv, "Webhook signing secret", CredentialKind.Secret),
        ],
        Settings = [CloseDays, IndexDelay],
        TestEvents = TestEvent,
        WebhookSigner = Sign,
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/zendesk/api/v2/tickets", (HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => CreateTicket(http, ctx)));
        routes.MapGet("/zendesk/api/v2/tickets/{id}", (string id, HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => GetTicket(id, http, ctx)));
        routes.MapPut("/zendesk/api/v2/tickets/{id}", (string id, HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => UpdateTicket(id, http, ctx)));
        routes.MapGet("/zendesk/api/v2/tickets/{id}/comments", (string id, HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => ListComments(id, http, ctx)));
        routes.MapGet("/zendesk/api/v2/users/{id}", (string id, HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => GetUser(id, http, ctx)));
        routes.MapGet("/zendesk/api/v2/search", (HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => Search(http, ctx)));
        routes.MapPost("/zendesk/api/v2/uploads", (HttpContext http, ConnectorContext ctx) => Guard(http, ctx, () => Upload(http, ctx)));
        routes.MapGet("/zendesk/attachments/{id}/{fileName}", (string id, string fileName, HttpContext http, ConnectorContext ctx) =>
            Guard(http, ctx, () => AttachmentContent(id, fileName, http, ctx)));
    }

    // ---- auth ----

    private static async Task<IResult> Guard(HttpContext http, ConnectorContext ctx, Func<Task<IResult>> handler)
    {
        var (email, token) = Basic(http);
        return ctx.CredentialMatches(EmailEnv, email) && ctx.CredentialMatches(TokenEnv, token)
            ? await handler()
            : Results.Json(new { error = "Couldn't authenticate you" }, Json, statusCode: 401);
    }

    private static Task<IResult> Guard(HttpContext http, ConnectorContext ctx, Func<IResult> handler) =>
        Guard(http, ctx, () => Task.FromResult(handler()));

    /// "{email}/token" and the API token, or nulls.
    private static (string? Email, string? Token) Basic(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return (null, null);
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch (FormatException) { return (null, null); }
        var split = decoded.IndexOf(':');
        if (split < 0) return (null, null);
        var user = decoded[..split];
        return (user.EndsWith("/token", StringComparison.Ordinal) ? user[..^"/token".Length] : null, decoded[(split + 1)..]);
    }

    // ---- tickets ----

    private static async Task<IResult> CreateTicket(HttpContext http, ConnectorContext ctx)
    {
        if (await ReadRoot(http, "ticket") is not { } t) return BadJson();
        var comment = Obj(t, "comment");
        var body = comment is { } c ? Str(c, "body") ?? StripHtml(Str(c, "html_body")) : Str(t, "description");
        if (string.IsNullOrWhiteSpace(body)) return Invalid("description", "Description: cannot be blank", "BlankValue");

        long requesterId;
        if (Long(t, "requester_id") is { } rid)
        {
            if (ctx.Store.Get<ZendeskUser>(rid.ToString()) is null) return Invalid("requester", "Requester: is invalid", "InvalidValue");
            requesterId = rid;
        }
        else if (Obj(t, "requester") is { } r)
        {
            if (Str(r, "email") is null && Str(r, "name") is null) return Invalid("requester", "Requester: name or email is required", "BlankValue");
            requesterId = UserFor(ctx, Str(r, "name"), Str(r, "email"), Str(r, "phone")).Id;
        }
        else requesterId = ApiUser(ctx).Id;

        var (status, raw) = StatusOf(t);
        if (raw is not null && status is null or TicketStatus.closed) return Invalid("status", $"Status: {raw} is not valid", "InvalidValue");
        if (TakeUploads(ctx, comment) is not { } attachments) return Invalid("uploads", "Uploads: token is invalid or expired", "InvalidValue");

        var now = ctx.Now;
        var ticket = new ZendeskTicket
        {
            Id = TicketBase + ctx.Store.All<ZendeskTicket>().Count + 1,
            Status = status ?? TicketStatus.@new,
            ExternalId = Str(t, "external_id"),
            Subject = Str(t, "subject") ?? body[..Math.Min(body.Length, 80)],
            Description = body,
            RequesterId = requesterId,
            SubmitterId = ApiUser(ctx).Id,
            BrandId = Long(t, "brand_id"),
            TicketFormId = Long(t, "ticket_form_id"),
            Tags = StrList(t, "tags") ?? [],
            CreatedAt = now,
            UpdatedAt = now,
            IndexedAt = now.AddSeconds(SettingOr(ctx, IndexDelay, 0)),
        };
        ApplyFields(ticket, Fields(t));
        if (ticket.Status == TicketStatus.solved) ticket.CloseAt = now.AddDays(SettingOr(ctx, CloseDays, 4));
        ctx.Create(Ticket, ticket, "api");
        AddComment(ctx, ticket, body, comment is { } cc ? Str(cc, "html_body") : null, comment is not { } pc || Bool(pc, "public") != false, ApiUserId, attachments);
        http.Annotate(entityId: ticket.Key);
        return Results.Json(new { ticket = View(http, ticket) }, Json, statusCode: 201);
    }

    private static IResult GetTicket(string id, HttpContext http, ConnectorContext ctx) =>
        ctx.Store.Get<ZendeskTicket>(id) is { } t ? Results.Json(new { ticket = View(http, t) }, Json) : NotFound();

    private static async Task<IResult> UpdateTicket(string id, HttpContext http, ConnectorContext ctx)
    {
        if (ctx.Store.Get<ZendeskTicket>(id) is not { } ticket) return NotFound();
        if (await ReadRoot(http, "ticket") is not { } t) return BadJson();
        // A closed ticket is read-only.
        if (ticket.Status == TicketStatus.closed) return Invalid("status", "Status: closed prevents ticket update", "ClosedPreventsUpdate");

        var (to, raw) = StatusOf(t);
        if (raw is not null && (to is null || (to != ticket.Status && !Ticket.CanMove(ticket.Status, to.Value))))
            return Invalid("status", $"Status: {raw} is not valid from {ticket.Status}", "InvalidValue");
        var comment = Obj(t, "comment");
        if (TakeUploads(ctx, comment) is not { } attachments) return Invalid("uploads", "Uploads: token is invalid or expired", "InvalidValue");

        if (Str(t, "external_id") is { } externalId) ticket.ExternalId = externalId;
        if (Str(t, "subject") is { } subject) ticket.Subject = subject;
        if (StrList(t, "tags") is { } tags) ticket.Tags = tags;
        ApplyFields(ticket, Fields(t));
        ticket.UpdatedAt = ctx.Now;
        if (comment is { } c && (Str(c, "body") ?? StripHtml(Str(c, "html_body"))) is { Length: > 0 } body)
            AddComment(ctx, ticket, body, Str(c, "html_body"), Bool(c, "public") != false, ApiUserId, attachments);

        if (to is { } s && s != ticket.Status) await ChangeStatus(ctx, ticket, s, "api", ApiUserId);
        else ctx.Save(ticket.Key, ticket);

        return Results.Json(new
        {
            ticket = View(http, ticket),
            audit = new { id = CommentBase + ctx.Store.All<ZendeskComment>().Count + 1, ticket_id = ticket.Id, created_at = Ts(ctx.Now), author_id = ApiUserId, events = Array.Empty<object>() },
        }, Json);
    }

    /// Values replace the stored ones; an option field also swaps its mirrored tag.
    private static void ApplyFields(ZendeskTicket ticket, IEnumerable<CustomField> fields)
    {
        foreach (var f in fields)
        {
            var old = ticket.Field(f.Id);
            ticket.CustomFields.RemoveAll(x => x.Id == f.Id);
            var value = string.IsNullOrEmpty(f.Value) ? null : f.Value;
            if (value is not null) ticket.CustomFields.Add(f with { Value = value });
            if (!ZendeskAccount.Dropdowns.Contains(f.Id)) continue;
            if (old is not null) ticket.Tags.Remove(old);
            if (value is not null && !ticket.Tags.Contains(value)) ticket.Tags.Add(value);
        }
    }

    private static object View(HttpContext http, ZendeskTicket t)
    {
        var fields = t.CustomFields.Select(f => new { id = f.Id, value = f.Value }).ToList();
        return new
        {
            url = $"{Base(http)}/api/v2/tickets/{t.Id}.json",
            id = t.Id,
            external_id = t.ExternalId,
            via = new { channel = "api" },
            created_at = Ts(t.CreatedAt),
            updated_at = Ts(t.UpdatedAt),
            type = (string?)null,
            subject = t.Subject,
            raw_subject = t.Subject,
            description = t.Description,
            priority = (string?)null,
            status = t.Status.ToString(),
            requester_id = t.RequesterId,
            submitter_id = t.SubmitterId,
            assignee_id = (long?)null,
            organization_id = (long?)null,
            group_id = (long?)null,
            is_public = true,
            tags = t.Tags,
            custom_fields = fields,
            fields,
            brand_id = t.BrandId,
            ticket_form_id = t.TicketFormId,
        };
    }

    // ---- comments ----

    internal static ZendeskComment AddComment(ConnectorContext ctx, ZendeskTicket ticket, string body, string? html, bool isPublic, long authorId,
        List<Attachment> attachments)
    {
        if (authorId == AgentUserId) AgentUser(ctx);
        var comment = new ZendeskComment
        {
            Id = CommentBase + ctx.Store.All<ZendeskComment>().Count + 1,
            TicketId = ticket.Id,
            Body = body,
            HtmlBody = html ?? $"<div class=\"zd-comment\" dir=\"auto\"><p>{System.Net.WebUtility.HtmlEncode(body)}</p></div>",
            Public = isPublic,
            AuthorId = authorId,
            CreatedAt = ctx.Now,
            Attachments = attachments,
        };
        ctx.Save(comment.Key, comment);
        return comment;
    }

    private static IResult ListComments(string id, HttpContext http, ConnectorContext ctx)
    {
        if (ctx.Store.Get<ZendeskTicket>(id) is null) return NotFound();
        var q = http.Request.Query;
        var all = ctx.Store.All<ZendeskComment>().Where(c => c.TicketId.ToString() == id).OrderBy(c => c.Id).ToList();
        if (q["sort_order"] == "desc") all.Reverse();
        var (page, perPage) = (Int(q["page"], 1), Math.Clamp(Int(q["per_page"], 100), 1, 100));
        var items = all.Skip((page - 1) * perPage).Take(perPage).Select(c => CommentView(http, c)).ToList();
        var url = $"{Base(http)}/api/v2/tickets/{id}/comments.json?per_page={perPage}" + (q["sort_order"] == "desc" ? "&sort_order=desc" : "");
        return Results.Json(new
        {
            comments = items,
            next_page = page * perPage < all.Count ? $"{url}&page={page + 1}" : null,
            previous_page = page > 1 ? $"{url}&page={page - 1}" : null,
            count = all.Count,
        }, Json);
    }

    private static object CommentView(HttpContext http, ZendeskComment c) => new
    {
        id = c.Id,
        type = "Comment",
        author_id = c.AuthorId,
        body = c.Body,
        html_body = c.HtmlBody,
        plain_body = c.Body,
        @public = c.Public,
        attachments = c.Attachments.Select(a => AttachmentView(http, a)).ToList(),
        audit_id = c.Id,
        via = new { channel = "api" },
        created_at = Ts(c.CreatedAt),
    };

    // ---- users ----

    private static IResult GetUser(string id, HttpContext http, ConnectorContext ctx) =>
        ctx.Store.Get<ZendeskUser>(id) is { } u
            ? Results.Json(new
            {
                user = new
                {
                    id = u.Id, url = $"{Base(http)}/api/v2/users/{u.Id}.json", name = u.Name, email = u.Email, phone = u.Phone,
                    role = u.Role, active = true, created_at = Ts(u.CreatedAt), updated_at = Ts(u.CreatedAt),
                },
            }, Json)
            : NotFound();

    /// The requester is found by e-mail or created, as Zendesk does with requester {name, email}.
    private static ZendeskUser UserFor(ConnectorContext ctx, string? name, string? email, string? phone)
    {
        if (email is not null && ctx.Store.All<ZendeskUser>().FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)) is { } known)
            return known;
        var user = new ZendeskUser
        {
            Id = UserBase + ctx.Store.All<ZendeskUser>().Count + 1,
            Name = name ?? email!.Split('@')[0],
            Email = email,
            Phone = phone,
            CreatedAt = ctx.Now,
        };
        ctx.Save(user.Key, user);
        return user;
    }

    private static ZendeskUser ApiUser(ConnectorContext ctx) => FixedUser(ctx, ApiUserId, "Acme API", "api@example.com");
    private static ZendeskUser AgentUser(ConnectorContext ctx) => FixedUser(ctx, AgentUserId, "Agent (Chameleon)", "agent@example.com");

    private static ZendeskUser FixedUser(ConnectorContext ctx, long id, string name, string email)
    {
        if (ctx.Store.Get<ZendeskUser>(id.ToString()) is { } user) return user;
        user = new ZendeskUser { Id = id, Name = name, Email = email, Role = "admin", CreatedAt = ctx.Now };
        ctx.Save(user.Key, user);
        return user;
    }

    private static object AttachmentView(HttpContext http, Attachment a) => new
    {
        id = a.Id, file_name = a.FileName, content_type = a.ContentType, size = a.Size,
        content_url = $"{Base(http)}/attachments/{a.Id}/{Uri.EscapeDataString(a.FileName)}",
        mapped_content_url = $"{Base(http)}/attachments/{a.Id}/{Uri.EscapeDataString(a.FileName)}",
        inline = false, deleted = false,
    };

    // ---- helpers ----

    private static string Base(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/zendesk";

    /// Zendesk timestamps: UTC, seconds, "Z".
    internal static string Ts(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    internal static int SettingOr(ConnectorContext ctx, ConnectorSetting setting, int fallback) =>
        int.TryParse(ctx.Setting(setting.Key), out var v) && v >= 0 ? v : fallback;

    private static IResult NotFound() => Results.Json(new { error = "RecordNotFound", description = "Not found" }, Json, statusCode: 404);

    private static IResult BadJson() => Results.Json(new { error = "Unprocessable Entity", description = "Server could not parse JSON" }, Json, statusCode: 400);

    internal static IResult Invalid(string field, string description, string error) => Results.Json(new
    {
        error = "RecordInvalid",
        description = "Record validation errors",
        details = new Dictionary<string, object> { [field] = new[] { new { description, error } } },
    }, Json, statusCode: 422);

    private static async Task<JsonElement?> ReadRoot(HttpContext http, string root)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(root, out var r) && r.ValueKind == JsonValueKind.Object
                ? r.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (TicketStatus? Status, string? Raw) StatusOf(JsonElement t) =>
        Str(t, "status") is { } raw
            ? (Enum.GetValues<TicketStatus>().Cast<TicketStatus?>().FirstOrDefault(s => s.ToString() == raw.ToLowerInvariant()), raw)
            : (null, null);

    private static IEnumerable<CustomField> Fields(JsonElement t) =>
        (t.TryGetProperty("custom_fields", out var a) || t.TryGetProperty("fields", out a)) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object && Long(f, "id") is not null)
                .Select(f => new CustomField(Long(f, "id")!.Value, f.TryGetProperty("value", out var v) ? Text(v) : null)).ToList()
            : [];

    /// Any JSON scalar is stored as its text.
    private static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    internal static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt64(out var n) : long.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : null, out n)) ? n : null;

    internal static bool? Bool(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static List<string>? StrList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Distinct().ToList()
            : null;

    private static int Int(string? value, int fallback) => int.TryParse(value, out var n) && n > 0 ? n : fallback;

    private static string? StripHtml(string? html) => html is null ? null : HtmlTag().Replace(html, "").Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();
}
