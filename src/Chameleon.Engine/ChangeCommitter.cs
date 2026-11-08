namespace Chameleon.Engine;

/// Everything one request wrote in its sandbox: the sandbox itself when its clock moved, the
/// entities, the journal entries and the webhook deliveries they caused.
public sealed record ChangeSet(
    string SandboxId,
    Sandbox? Sandbox,
    IReadOnlyList<EntityChange> Entities,
    IReadOnlyList<Interaction> Journal,
    IReadOnlyList<WebhookDelivery> Deliveries);

/// Persists a request's changes together. Postgres does it in one transaction; the in-memory
/// default writes store by store (entities first: a version conflict stops everything).
public interface IChangeCommitter
{
    void Commit(ChangeSet changes);
}

public sealed class StoreChangeCommitter(
    ISandboxStore sandboxes, IEntityStore entities, IInteractionStore interactions, IWebhookOutbox outbox)
    : IChangeCommitter
{
    public void Commit(ChangeSet changes)
    {
        if (changes.Entities.Count > 0) entities.Write(changes.SandboxId, changes.Entities);
        if (changes.Sandbox is not null) sandboxes.Put(changes.Sandbox);
        if (changes.Journal.Count > 0) interactions.Append(changes.Journal);
        if (changes.Deliveries.Count > 0) outbox.Enqueue(changes.Deliveries);
    }
}
