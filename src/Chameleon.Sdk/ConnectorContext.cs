using System.Text.Json;
using Chameleon.Engine;

namespace Chameleon.Sdk;

/// Everything a connector handler needs, scoped to the current request and sandbox. Writes go
/// through the request scope and land together when the request completes, webhook deliveries
/// included.
public sealed class ConnectorContext(
    RequestScope scope, SandboxClock clock, SandboxContext sandbox, IFaultStore faults, IEnumerable<IConnector> connectors,
    SecretBox secrets)
{
    private const int DefaultReorderDelayMs = 5000;

    private ConnectorStore store => Store;
    private RequestScope journal => scope;

    /// The connector being served; set by the host before the handler or clock hook runs.
    public string Connector { get; set; } = "";

    public ConnectorStore Store { get; } = new(scope);
    public SandboxClock Clock => clock;
    public DateTimeOffset Now => clock.Now;
    public string SandboxId => sandbox.Id;

    public ConnectorDescriptor Descriptor =>
        connectors.First(c => c.Name == Connector).Describe();

    /// This connector's active subscription in the sandbox, or null.
    public ConnectorSubscription? Subscription =>
        store.Get<ConnectorSubscription>(Connector) is { Active: true } s ? s : null;

    /// A setting the service under test filled in, or null.
    public string? Setting(string key) =>
        Subscription?.Settings.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;

    /// True when the issued credential matches. Without a subscription only "default" accepts anything.
    public bool CredentialMatches(string env, string? given) =>
        Subscription is { } s ? s.Matches(Descriptor, secrets, env, given) : sandbox.Id == Sandbox.Default;

    /// Persists a new entity in its initial state and records "criada -> {state}".
    public void Create<TEntity, TState>(Lifecycle<TEntity, TState> lifecycle, TEntity entity, string? reason = null)
        where TEntity : class where TState : struct, Enum
    {
        var key = lifecycle.KeyOf(entity);
        store.Put(key, entity);
        journal.Record(lifecycle.TransitionRecord(Connector, key, null, lifecycle.StateOf(entity), Now, reason));
        Changed(lifecycle, key, lifecycle.StateOf(entity));
    }

    /// Moves an entity along its lifecycle. Throws TransitionConflictException (-> 409) when the
    /// lifecycle does not allow it; otherwise persists and records the transition.
    public void Move<TEntity, TState>(Lifecycle<TEntity, TState> lifecycle, TEntity entity, TState to, string? reason = null)
        where TEntity : class where TState : struct, Enum
    {
        var from = lifecycle.StateOf(entity);
        lifecycle.EnsureCanMove(from, to);
        lifecycle.SetState(entity, to);
        var key = lifecycle.KeyOf(entity);
        store.Put(key, entity);
        journal.Record(lifecycle.TransitionRecord(Connector, key, from, to, Now, reason));
        Changed(lifecycle, key, to);
    }

    private void Changed<TEntity, TState>(Lifecycle<TEntity, TState> lifecycle, string key, TState state)
        where TEntity : class where TState : struct, Enum =>
        scope.Publish(new LiveEvent(LiveEvent.EntityChanged,
            new { connector = Connector, entityType = lifecycle.EntityType, key, state = state.ToString() }));

    /// True while the operator holds this route (e.g. settlement) with an armed Hold.
    public bool IsHeld(string route) => faults.IsHeld(sandbox.Id, Connector, Now, route);

    /// Entities of this type due now (TTL candidates for OnClockAdvancedAsync).
    public IReadOnlyCollection<T> Due<T>() where T : class => store.Due<T>(Now);

    /// Persists changes to an entity that do not move its state.
    public void Save<TEntity>(string key, TEntity entity) where TEntity : class => store.Put(key, entity);

    /// Queues a webhook to the app under test, delivered with retries after the request commits;
    /// or records that none is configured (the note, when given, says why). An armed webhook fault
    /// (delay, reorder, duplicate) applies here.
    public Task NotifyAsync(string entityId, string? url, object payload, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            var missing = Subscription?.Missing(Descriptor) ?? [];
            journal.Record(new Interaction(InteractionKind.OutboundWebhook, Connector, entityId, Now, "webhook not sent",
                missing.Count > 0 ? $"pending configuration: missing {string.Join(", ", missing)}" : note ?? "no webhook configured"));
            return Task.CompletedTask;
        }

        var policy = sandbox.Sandbox.WebhookPolicy
                     ?? connectors.FirstOrDefault(c => c.Name == Connector)?.Describe().Webhooks
                     ?? WebhookPolicy.Default;
        var hasFault = faults.TryConsume(sandbox.Id, Connector, IFaultStore.WebhookRoute, Now, out var fault);
        // ponytail: the fault use is spent outside the request transaction (a failed commit still
        // counts it); the panel hears about it only after the commit, like every other event.
        if (hasFault)
            scope.Publish(new LiveEvent(LiveEvent.FaultChanged,
                LiveEvents.Fault(Connector, IFaultStore.WebhookRoute, fault.Kind, fault.RemainingAfterUse)));
        var delayMs = hasFault && fault.Kind is FaultKind.Latency or FaultKind.Reorder
            ? fault.DelayMs ?? (fault.Kind == FaultKind.Reorder ? DefaultReorderDelayMs : 1000)
            : 0;
        var faultNote = !hasFault ? null : fault.Kind switch
        {
            FaultKind.Latency => $"armed delay of {delayMs} ms",
            FaultKind.Reorder => $"out of order: delayed {delayMs} ms",
            FaultKind.Duplicate => "duplicated by an armed fault",
            _ => null,
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var copies = hasFault && fault.Kind == FaultKind.Duplicate ? 2 : 1;
        for (var i = 0; i < copies; i++)
            scope.Enqueue(new WebhookDelivery
            {
                SandboxId = sandbox.Id,
                Connector = Connector,
                EntityId = entityId,
                CorrelationId = scope.CorrelationId,
                Url = url,
                Payload = body,
                MaxAttempts = policy.MaxAttempts,
                BaseDelayMs = policy.BaseDelayMs,
                TimeoutMs = policy.TimeoutMs,
                Note = string.Join("; ", new[] { note, faultNote }.Where(n => n is not null)) is { Length: > 0 } n ? n : null,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(delayMs),
            });
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
