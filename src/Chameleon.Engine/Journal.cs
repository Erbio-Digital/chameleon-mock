namespace Chameleon.Engine;

public enum InteractionKind { InboundRequest, ControlAction, StateTransition, OutboundWebhook }

/// One recorded thing that happened: a call in, a control action, a state change, or a webhook out.
/// The positional part is what connectors fill; the HTTP details are filled by the capture
/// pipeline and the webhook sender.
public sealed record Interaction(
    InteractionKind Kind,
    string Connector,
    string? EntityId,
    DateTimeOffset At,
    string Summary,
    string? Detail = null)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// Recording order across the store (assigned on append): the feed's cursor.
    public long Seq { get; set; }
    public string SandboxId { get; init; } = Sandbox.Default;
    public string? CorrelationId { get; init; }
    public string? Method { get; init; }
    public string? Path { get; init; }
    public int? StatusCode { get; init; }
    public long? DurationMs { get; init; }
    public Dictionary<string, string>? RequestHeaders { get; init; }
    public string? RequestBody { get; init; }
    public string? ResponseBody { get; init; }
    public bool Truncated { get; init; }
    public string? Fault { get; init; }
    public string? EntityType { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }

    /// A transition the sandbox clock made on its own (expiry, settlement), not caused by a call.
    public bool ByClock { get; init; }

    /// Wall-clock time it was recorded (At is the sandbox's virtual time); drives retention.
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// Typed filter over the journal. Feeds both the debug query and the verify/assert API
/// ("endpoint X called 1x", "webhook fired for entity Y").
public sealed record InteractionQuery(
    string? SandboxId = null,
    InteractionKind? Kind = null,
    string? Connector = null,
    string? EntityId = null,
    string? CorrelationId = null,
    string? SummaryContains = null,
    int? StatusCode = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Id = null,
    long? AfterSeq = null,
    long? BeforeSeq = null,
    // With a limit: newest first, or oldest first after AfterSeq (resuming a paused feed).
    int? Limit = null,
    // 2, 4 or 5: only calls answered 2xx, 4xx or 5xx.
    int? StatusClass = null,
    bool? ByClock = null)
{
    public bool Matches(Interaction e) =>
        (SandboxId is null || e.SandboxId == SandboxId)
        && (Id is null || e.Id == Id)
        && (AfterSeq is null || e.Seq > AfterSeq)
        && (BeforeSeq is null || e.Seq < BeforeSeq)
        && (Kind is null || e.Kind == Kind)
        && (Connector is null || e.Connector == Connector)
        && (EntityId is null || e.EntityId == EntityId)
        && (CorrelationId is null || e.CorrelationId == CorrelationId)
        && (SummaryContains is null || e.Summary.Contains(SummaryContains))
        && (StatusCode is null || e.StatusCode == StatusCode)
        && (StatusClass is null || e.StatusCode / 100 == StatusClass)
        && (ByClock is null || e.ByClock == ByClock)
        && (From is null || e.At >= From)
        && (To is null || e.At <= To);
}

public sealed record VerifyResult(int Count, IReadOnlyList<Interaction> Matches);

/// Persistence port for the journal (in-memory, Postgres).
public interface IInteractionStore
{
    void Append(IReadOnlyCollection<Interaction> entries);
    IReadOnlyList<Interaction> Find(InteractionQuery query);
    void DeleteSandbox(string sandboxId);

    /// Retention: removes entries recorded before the given wall-clock instant.
    int DeleteRecordedBefore(DateTimeOffset before);
}

public sealed class InMemoryInteractionStore : IInteractionStore
{
    private readonly List<Interaction> _entries = new();
    private readonly object _gate = new();
    private long _seq;

    public void Append(IReadOnlyCollection<Interaction> entries)
    {
        lock (_gate)
            foreach (var e in entries)
            {
                e.Seq = ++_seq;
                _entries.Add(e);
            }
    }

    public IReadOnlyList<Interaction> Find(InteractionQuery query)
    {
        lock (_gate)
        {
            var matches = _entries.Where(query.Matches);
            if (query.Limit is not { } limit) return matches.ToList();
            return query.AfterSeq is not null
                ? matches.Take(limit).ToList()
                : matches.Reverse().Take(limit).ToList();
        }
    }

    public void DeleteSandbox(string sandboxId)
    {
        lock (_gate) _entries.RemoveAll(e => e.SandboxId == sandboxId);
    }

    public int DeleteRecordedBefore(DateTimeOffset before)
    {
        lock (_gate) return _entries.RemoveAll(e => e.RecordedAt < before);
    }
}
