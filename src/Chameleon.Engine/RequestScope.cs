namespace Chameleon.Engine;

/// Everything one request (or one tick) writes in its sandbox, committed together at the end:
/// the sandbox (if its clock moved), entities, journal entries and webhook deliveries, all
/// stamped with the sandbox and the correlation id. Nothing reaches the stores if the request
/// throws. Concrete on purpose (not an IUnitOfWork port): the committer makes it one DB
/// transaction in Postgres.
public sealed class RequestScope(SandboxContext sandbox, IEntityStore entities, IChangeCommitter committer, ILiveEventBus live,
    IWebhookSignal webhookSignal)
{
    private readonly List<LiveEvent> _live = new();
    // Every entity this request touched (one instance per key); only the ones Put are written.
    private readonly Dictionary<(Type, string), object> _entities = new();
    private readonly HashSet<(Type, string)> _dirty = new();
    // Version of each entity as this request read it (null = it did not exist): the commit requires it.
    private readonly Dictionary<(Type, string), long?> _read = new();
    private readonly List<Interaction> _journal = new();
    private readonly List<WebhookDelivery> _deliveries = new();

    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    /// The writes come from the sandbox clock (ticker or advance), not from a call: transitions
    /// recorded in this scope are marked so the feed shows them on their own.
    public bool ByClock { get; set; }

    public void Put<T>(string id, T entity) where T : class
    {
        _entities[(typeof(T), id)] = entity;
        _dirty.Add((typeof(T), id));
    }

    public T? Get<T>(string id) where T : class
    {
        if (_entities.TryGetValue((typeof(T), id), out var known)) return (T)known;

        var stored = entities.Get(sandbox.Id, typeof(T), id);
        _read.TryAdd((typeof(T), id), stored?.Version);
        if (stored is null) return null;
        // Same instance for the rest of the request: mutations after a Put are what gets committed.
        _entities[(typeof(T), id)] = stored.Entity;
        return (T)stored.Entity;
    }

    public IReadOnlyCollection<T> All<T>() where T : class => Merge<T>(entities.All(sandbox.Id, typeof(T)), _ => true);

    public IReadOnlyCollection<T> Due<T>(DateTimeOffset now) where T : class =>
        Merge<T>(entities.Due(sandbox.Id, typeof(T), now), e => EntityJson.DueAt(e) <= now);

    /// Stored results plus what this request wrote, always the request's own instance per key.
    private IReadOnlyCollection<T> Merge<T>(IReadOnlyCollection<StoredEntity> stored, Func<object, bool> dirtyFilter) where T : class
    {
        foreach (var s in stored)
        {
            if (!_entities.TryAdd((typeof(T), s.Key), s.Entity)) continue;
            _read.TryAdd((typeof(T), s.Key), s.Version);
        }
        var keys = stored.Select(s => s.Key).ToHashSet();
        return _entities
            .Where(kv => kv.Key.Item1 == typeof(T)
                         && (keys.Contains(kv.Key.Item2) || (_dirty.Contains(kv.Key) && dirtyFilter(kv.Value))))
            .Select(kv => (T)kv.Value)
            .ToList();
    }

    public void Record(Interaction entry) => _journal.Add(Stamp(entry));

    /// The call that caused this request's writes is only known once it finishes, but it
    /// comes first in the timeline. A call whose route does not name the entity (a POST that
    /// creates it) belongs to the one entity it changed, so filtering by that entity finds it.
    public void RecordCause(Interaction entry)
    {
        if (entry.EntityId is null && _journal.Select(j => j.EntityId).OfType<string>().Distinct().ToList() is [var only])
            entry = entry with { EntityId = only };
        _journal.Insert(0, Stamp(entry));
    }

    public void Enqueue(WebhookDelivery delivery) => _deliveries.Add(delivery);

    /// A live event that only goes out if this request commits (e.g. an entity changed state).
    public void Publish(LiveEvent liveEvent) => _live.Add(liveEvent);

    private Interaction Stamp(Interaction entry) =>
        entry with
        {
            SandboxId = sandbox.Id,
            CorrelationId = entry.CorrelationId ?? CorrelationId,
            RecordedAt = DateTimeOffset.UtcNow,
            ByClock = entry.ByClock || (ByClock && entry.Kind == InteractionKind.StateTransition),
        };

    /// Drops everything buffered so far (the request failed); later records still commit.
    public void Discard()
    {
        sandbox.Changed = false;
        ClearBuffers();
    }

    /// Throws ConcurrencyConflictException (nothing written) when an entity it read changed meanwhile.
    public void Commit()
    {
        var changes = _dirty
            .Select(key => _read.TryGetValue(key, out var version)
                ? new EntityChange(key.Item1, key.Item2, _entities[key], Checked: true, ExpectedVersion: version)
                : new EntityChange(key.Item1, key.Item2, _entities[key]))
            .ToList();
        if (!sandbox.Changed && changes.Count == 0 && _journal.Count == 0 && _deliveries.Count == 0) return;

        committer.Commit(new ChangeSet(sandbox.Id, sandbox.Changed ? sandbox.Sandbox : null, changes, _journal.ToList(), _deliveries.ToList()));

        // Committed: the panel sees it now, in the order it happened.
        var events = _journal.Select(j => new LiveEvent(LiveEvent.Interaction, j))
            .Concat(_live)
            .Concat(_deliveries.Select(d => new LiveEvent(LiveEvent.WebhookUpdated, LiveEvents.Webhook(d))))
            .ToList();
        if (sandbox.Changed) events.Add(new LiveEvent(LiveEvent.SandboxChanged, LiveEvents.Sandbox(sandbox.Sandbox, DateTimeOffset.UtcNow)));
        live.Publish(sandbox.Id, events);
        foreach (var delivery in _deliveries) webhookSignal.Signal(delivery);
        sandbox.Changed = false;
        ClearBuffers();
    }

    private void ClearBuffers()
    {
        _entities.Clear();
        _dirty.Clear();
        _read.Clear();
        _journal.Clear();
        _deliveries.Clear();
        _live.Clear();
    }
}
