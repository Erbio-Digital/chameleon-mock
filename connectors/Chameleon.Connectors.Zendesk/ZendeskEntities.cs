using Chameleon.Engine;

namespace Chameleon.Connectors.Zendesk;

/// Zendesk ticket statuses, with the provider's own (lowercase) names.
public enum TicketStatus { @new, open, pending, hold, solved, closed }

public sealed record CustomField(long Id, string? Value);

public sealed record Attachment(long Id, string FileName, string ContentType, long Size);

public sealed class ZendeskTicket : IHasNaturalKey, IHasDueAt
{
    public long Id { get; init; }
    public string Key => Id.ToString();
    public string NaturalKeyProperty => nameof(Key);
    public TicketStatus Status { get; set; } = TicketStatus.@new;
    public string? ExternalId { get; set; }
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public long RequesterId { get; set; }
    public long SubmitterId { get; set; }
    public long? BrandId { get; set; }
    public long? TicketFormId { get; set; }
    public List<CustomField> CustomFields { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// Before this instant the search index does not know the ticket (ATRASO_INDEXACAO_SEGUNDOS).
    public DateTimeOffset IndexedAt { get; init; }
    /// Solved: when the default automation closes it (DIAS_PARA_FECHAR).
    public DateTimeOffset? CloseAt { get; set; }
    /// Last status_changed event sent, resent by the "invalid signature" action.
    public string? LastEvent { get; set; }

    public DateTimeOffset? DueAt => Status == TicketStatus.solved ? CloseAt : null;

    public string? Field(long id) => CustomFields.FirstOrDefault(f => f.Id == id)?.Value;
}

public sealed class ZendeskComment : IHasNaturalKey
{
    public long Id { get; init; }
    public string Key => Id.ToString();
    public string NaturalKeyProperty => nameof(Key);
    public long TicketId { get; init; }
    public string Body { get; init; } = "";
    public string HtmlBody { get; init; } = "";
    public bool Public { get; init; } = true;
    public long AuthorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<Attachment> Attachments { get; init; } = [];
}

public sealed class ZendeskUser : IHasNaturalKey
{
    public long Id { get; init; }
    public string Key => Id.ToString();
    public string NaturalKeyProperty => nameof(Key);
    public string Name { get; init; } = "";
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string Role { get; init; } = "end-user";
    public DateTimeOffset CreatedAt { get; init; }
}

/// POST /uploads: the token a comment references; used once, valid for 3 days.
public sealed class ZendeskUpload : IHasNaturalKey
{
    public required string Token { get; init; }
    public string NaturalKeyProperty => nameof(Token);
    public required Attachment Attachment { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool Used { get; set; }
}

/// The bytes behind content_url (only up to ZendeskConnector.MaxStoredBytes).
public sealed class ZendeskAttachmentContent : IHasNaturalKey
{
    public long Id { get; init; }
    public string Key => Id.ToString();
    public string NaturalKeyProperty => nameof(Key);
    public string ContentBase64 { get; init; } = "";
}

/// Where the status_changed webhook goes (in the real provider, set in the admin).
public sealed class ZendeskWebhook : IHasNaturalKey
{
    public required string Evento { get; init; }
    public string NaturalKeyProperty => nameof(Evento);
    /// Null once removed (the row stays, like the other connectors' registrations).
    public string? Url { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
