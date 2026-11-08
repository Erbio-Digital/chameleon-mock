using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Chameleon.Engine;

namespace Chameleon.Storage.Postgres;

/// Shared writes: the stores and the transactional committer issue the same statements.
internal static class Sql
{
    public static string TypeName(Type type) => type.FullName ?? type.Name;

    public static string? PolicyJson(WebhookPolicy? policy) => policy is null ? null : JsonSerializer.Serialize(policy);

    public static string HeadersJson(List<WebhookHeader> headers) => JsonSerializer.Serialize(headers);

    public static void UpsertSandbox(ChameleonDbContext db, Sandbox s) =>
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO sandboxes (id, persistent, clock_mode, clock_offset_seconds, frozen_at, created_at, expires_at, webhook_policy, webhook_headers)
            VALUES ({s.Id}, {s.Persistent}, {s.ClockMode.ToString()}, {s.ClockOffsetSeconds}, {s.FrozenAt}, {s.CreatedAt}, {s.ExpiresAt}, {PolicyJson(s.WebhookPolicy)}::jsonb, {HeadersJson(s.WebhookHeaders)}::jsonb)
            ON CONFLICT (id) DO UPDATE SET
                persistent = excluded.persistent, clock_mode = excluded.clock_mode,
                clock_offset_seconds = excluded.clock_offset_seconds, frozen_at = excluded.frozen_at,
                expires_at = excluded.expires_at, webhook_policy = excluded.webhook_policy, webhook_headers = excluded.webhook_headers
            """);

    /// Checked writes require the version the request read (or that the key still does not
    /// exist); any miss throws, and the caller's transaction rolls everything back.
    public static void WriteEntities(ChameleonDbContext db, string sandboxId, IReadOnlyCollection<EntityChange> changes)
    {
        foreach (var c in changes)
        {
            var type = TypeName(c.Type);
            var data = EntityJson.Serialize(c.Entity, c.Type);
            var due = EntityJson.DueAt(c.Entity);
            var now = DateTimeOffset.UtcNow;

            var rows = !c.Checked
                ? db.Database.ExecuteSqlInterpolated($"""
                    INSERT INTO entities (sandbox_id, type, key, data, due_at, version, updated_at)
                    VALUES ({sandboxId}, {type}, {c.Key}, {data}::jsonb, {due}, 1, {now})
                    ON CONFLICT (sandbox_id, type, key) DO UPDATE SET
                        data = excluded.data, due_at = excluded.due_at, updated_at = excluded.updated_at,
                        version = entities.version + 1
                    """)
                : c.ExpectedVersion is null
                    ? db.Database.ExecuteSqlInterpolated($"""
                        INSERT INTO entities (sandbox_id, type, key, data, due_at, version, updated_at)
                        VALUES ({sandboxId}, {type}, {c.Key}, {data}::jsonb, {due}, 1, {now})
                        ON CONFLICT (sandbox_id, type, key) DO NOTHING
                        """)
                    : db.Database.ExecuteSqlInterpolated($"""
                        UPDATE entities SET data = {data}::jsonb, due_at = {due}, updated_at = {now}, version = version + 1
                        WHERE sandbox_id = {sandboxId} AND type = {type} AND key = {c.Key} AND version = {c.ExpectedVersion}
                        """);

            if (rows == 0)
                throw new ConcurrencyConflictException($"{c.Type.Name} {c.Key} was changed by another operation");
        }
    }
}

public sealed class PostgresSandboxStore(IDbContextFactory<ChameleonDbContext> factory) : ISandboxStore
{
    public Sandbox? Get(string id)
    {
        using var db = factory.CreateDbContext();
        return db.Sandboxes.AsNoTracking().FirstOrDefault(s => s.Id == id);
    }

    public IReadOnlyList<Sandbox> All()
    {
        using var db = factory.CreateDbContext();
        return db.Sandboxes.AsNoTracking().OrderBy(s => s.CreatedAt).ToList();
    }

    public void Put(Sandbox sandbox)
    {
        using var db = factory.CreateDbContext();
        Sql.UpsertSandbox(db, sandbox);
    }

    public void Delete(string id)
    {
        using var db = factory.CreateDbContext();
        db.Sandboxes.Where(s => s.Id == id).ExecuteDelete();
    }
}

public sealed class PostgresEntityStore(IDbContextFactory<ChameleonDbContext> factory) : IEntityStore
{
    public StoredEntity? Get(string sandboxId, Type type, string id)
    {
        using var db = factory.CreateDbContext();
        var name = Sql.TypeName(type);
        var row = db.Entities.AsNoTracking().FirstOrDefault(e => e.SandboxId == sandboxId && e.Type == name && e.Key == id);
        return row is null ? null : Load(row, type);
    }

    public IReadOnlyCollection<StoredEntity> All(string sandboxId, Type type)
    {
        using var db = factory.CreateDbContext();
        var name = Sql.TypeName(type);
        return db.Entities.AsNoTracking().Where(e => e.SandboxId == sandboxId && e.Type == name)
            .AsEnumerable().Select(r => Load(r, type)).ToList();
    }

    public IReadOnlyCollection<StoredEntity> Due(string sandboxId, Type type, DateTimeOffset now)
    {
        using var db = factory.CreateDbContext();
        var name = Sql.TypeName(type);
        return db.Entities.AsNoTracking().Where(e => e.SandboxId == sandboxId && e.Type == name && e.DueAt <= now)
            .AsEnumerable().Select(r => Load(r, type)).ToList();
    }

    public bool AnyDue(string sandboxId, DateTimeOffset now)
    {
        using var db = factory.CreateDbContext();
        return db.Entities.Any(e => e.SandboxId == sandboxId && e.DueAt <= now);
    }

    public void Write(string sandboxId, IReadOnlyCollection<EntityChange> changes)
    {
        using var db = factory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();
        Sql.WriteEntities(db, sandboxId, changes);
        tx.Commit();
    }

    public void DeleteSandbox(string sandboxId)
    {
        using var db = factory.CreateDbContext();
        db.Entities.Where(e => e.SandboxId == sandboxId).ExecuteDelete();
    }

    public int DeleteUpdatedBefore(Type type, DateTimeOffset before)
    {
        using var db = factory.CreateDbContext();
        var name = Sql.TypeName(type);
        return db.Entities.Where(e => e.Type == name && e.UpdatedAt < before).ExecuteDelete();
    }

    public void CopySandbox(string fromSandboxId, string toSandboxId)
    {
        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO entities (sandbox_id, type, key, data, due_at, version, updated_at)
            SELECT {toSandboxId}, type, key, data, due_at, 1, {DateTimeOffset.UtcNow} FROM entities WHERE sandbox_id = {fromSandboxId}
            """);
    }

    private static StoredEntity Load(EntityRow row, Type type) =>
        new(row.Key, EntityJson.Deserialize(row.Data, type), row.Version, row.UpdatedAt);
}

public sealed class PostgresInteractionStore(IDbContextFactory<ChameleonDbContext> factory) : IInteractionStore
{
    public void Append(IReadOnlyCollection<Interaction> entries)
    {
        using var db = factory.CreateDbContext();
        db.Interactions.AddRange(entries);
        db.SaveChanges();
    }

    public IReadOnlyList<Interaction> Find(InteractionQuery q)
    {
        using var db = factory.CreateDbContext();
        var query = db.Interactions.AsNoTracking();
        if (q.SandboxId is not null) query = query.Where(e => e.SandboxId == q.SandboxId);
        if (q.Kind is not null) query = query.Where(e => e.Kind == q.Kind);
        if (q.Connector is not null) query = query.Where(e => e.Connector == q.Connector);
        if (q.EntityId is not null) query = query.Where(e => e.EntityId == q.EntityId);
        if (q.CorrelationId is not null) query = query.Where(e => e.CorrelationId == q.CorrelationId);
        if (q.StatusCode is not null) query = query.Where(e => e.StatusCode == q.StatusCode);
        if (q.ByClock is not null) query = query.Where(e => e.ByClock == q.ByClock);
        if (q.StatusClass is { } c) query = query.Where(e => e.StatusCode >= c * 100 && e.StatusCode < c * 100 + 100);
        if (q.SummaryContains is not null) query = query.Where(e => e.Summary.Contains(q.SummaryContains));
        if (q.From is not null) query = query.Where(e => e.At >= q.From);
        if (q.To is not null) query = query.Where(e => e.At <= q.To);
        if (q.Id is not null) query = query.Where(e => e.Id == q.Id);
        if (q.AfterSeq is not null) query = query.Where(e => e.Seq > q.AfterSeq);
        if (q.BeforeSeq is not null) query = query.Where(e => e.Seq < q.BeforeSeq);
        if (q.Limit is not { } limit) return query.OrderBy(e => e.Seq).ToList();
        return (q.AfterSeq is not null ? query.OrderBy(e => e.Seq) : query.OrderByDescending(e => e.Seq)).Take(limit).ToList();
    }

    public void DeleteSandbox(string sandboxId)
    {
        using var db = factory.CreateDbContext();
        db.Interactions.Where(e => e.SandboxId == sandboxId).ExecuteDelete();
    }

    public int DeleteRecordedBefore(DateTimeOffset before)
    {
        using var db = factory.CreateDbContext();
        return db.Interactions.Where(e => e.RecordedAt < before).ExecuteDelete();
    }
}

public sealed class PostgresFaultStore(IDbContextFactory<ChameleonDbContext> factory) : IFaultStore
{
    public void Arm(string sandboxId, string connector, string route, Fault fault)
    {
        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO faults (sandbox_id, connector, route, kind, status, delay_ms, remaining, until)
            VALUES ({sandboxId}, {connector}, {route}, {fault.Kind.ToString()}, {fault.Status}, {fault.DelayMs}, {fault.Times}, {fault.Until})
            ON CONFLICT (sandbox_id, connector, route) DO UPDATE SET
                kind = excluded.kind, status = excluded.status, delay_ms = excluded.delay_ms, remaining = excluded.remaining,
                until = excluded.until
            """);
    }

    public void Clear(string sandboxId, string connector, string route)
    {
        using var db = factory.CreateDbContext();
        db.Faults.Where(f => f.SandboxId == sandboxId && f.Connector == connector && f.Route == route).ExecuteDelete();
    }

    public void DeleteSandbox(string sandboxId)
    {
        using var db = factory.CreateDbContext();
        db.Faults.Where(f => f.SandboxId == sandboxId).ExecuteDelete();
    }

    public IReadOnlyList<ArmedFault> List(string sandboxId)
    {
        using var db = factory.CreateDbContext();
        return db.Faults.AsNoTracking().Where(f => f.SandboxId == sandboxId && (f.Remaining > 0 || f.Until != null))
            .OrderBy(f => f.Connector).ThenBy(f => f.Route)
            .AsEnumerable()
            .Select(f => new ArmedFault(f.Connector, f.Route, new Fault(f.Kind, f.Status, f.DelayMs, f.Remaining, f.Until)))
            .ToList();
    }

    /// One statement: two instances never consume the same use. A row left at zero, or a timed one
    /// past its end, is inert. A timed fault does not count uses.
    public bool TryConsume(string sandboxId, string connector, string route, DateTimeOffset now, out Fault fault)
    {
        using var db = factory.CreateDbContext();
        var row = db.Faults.FromSqlInterpolated($"""
                UPDATE faults SET remaining = CASE WHEN until IS NULL THEN remaining - 1 ELSE remaining END
                WHERE sandbox_id = {sandboxId} AND connector = {connector} AND route = {route} AND kind <> 'Hold'
                  AND (CASE WHEN until IS NULL THEN remaining > 0 ELSE until > {now} END)
                RETURNING *
                """)
            .AsNoTracking()
            .AsEnumerable()
            .FirstOrDefault();

        fault = row is null ? null! : new Fault(row.Kind, row.Status, row.DelayMs, row.Until is null ? row.Remaining + 1 : row.Remaining, row.Until);
        return row is not null;
    }
}

public sealed class PostgresWebhookOutbox(IDbContextFactory<ChameleonDbContext> factory) : IWebhookOutbox
{
    public void Enqueue(IReadOnlyCollection<WebhookDelivery> deliveries)
    {
        using var db = factory.CreateDbContext();
        db.Deliveries.AddRange(deliveries);
        db.SaveChanges();
    }

    /// SKIP LOCKED: concurrent workers never take the same delivery; each lease gets a new token.
    public IReadOnlyList<WebhookDelivery> Lease(DateTimeOffset utcNow, TimeSpan leaseFor, int max)
    {
        using var db = factory.CreateDbContext();
        var until = utcNow + leaseFor;
        return db.Deliveries.FromSqlInterpolated($"""
                UPDATE webhook_deliveries SET status = 'Delivering', lease_until = {until}, lease_token = gen_random_uuid()::text
                WHERE id IN (
                    SELECT id FROM webhook_deliveries
                    WHERE (status = 'Pending' AND next_attempt_at <= {utcNow}) OR (status = 'Delivering' AND lease_until < {utcNow})
                    ORDER BY next_attempt_at
                    LIMIT {max}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .AsEnumerable()
            .ToList();
    }

    public WebhookDelivery? LeaseById(string sandboxId, string id, DateTimeOffset utcNow, TimeSpan leaseFor)
    {
        using var db = factory.CreateDbContext();
        var until = utcNow + leaseFor;
        return db.Deliveries.FromSqlInterpolated($"""
                UPDATE webhook_deliveries SET status = 'Delivering', lease_until = {until}, lease_token = gen_random_uuid()::text
                WHERE id = {id} AND sandbox_id = {sandboxId}
                  AND ((status = 'Pending' AND next_attempt_at <= {utcNow}) OR (status = 'Delivering' AND lease_until < {utcNow}))
                RETURNING *
                """)
            .AsNoTracking()
            .AsEnumerable()
            .SingleOrDefault();
    }

    public bool Complete(WebhookDelivery d, WebhookAttempt attempt, Interaction journal)
    {
        using var db = factory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();
        var rows = db.Database.ExecuteSqlInterpolated($"""
            UPDATE webhook_deliveries
            SET status = {d.Status.ToString()}, attempts = {d.Attempts}, next_attempt_at = {d.NextAttemptAt}, lease_until = NULL
            WHERE id = {d.Id} AND status = 'Delivering' AND lease_token = {d.LeaseToken}
            """);
        if (rows == 0) return false;

        db.Attempts.Add(attempt);
        db.Interactions.Add(journal);
        db.SaveChanges();
        tx.Commit();
        return true;
    }

    public bool Postpone(WebhookDelivery d, DateTimeOffset nextAttemptAt)
    {
        using var db = factory.CreateDbContext();
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE webhook_deliveries SET status = 'Pending', next_attempt_at = {nextAttemptAt}, lease_until = NULL
            WHERE id = {d.Id} AND status = 'Delivering' AND lease_token = {d.LeaseToken}
            """) > 0;
    }

    public IReadOnlyList<WebhookDelivery> Find(string sandboxId, string? entityId = null, DeliveryStatus? status = null)
    {
        using var db = factory.CreateDbContext();
        var query = db.Deliveries.AsNoTracking().Where(d => d.SandboxId == sandboxId);
        if (entityId is not null) query = query.Where(d => d.EntityId == entityId);
        if (status is not null) query = query.Where(d => d.Status == status);
        return query.OrderBy(d => d.CreatedAt).ToList();
    }

    public WebhookDelivery? Get(string sandboxId, string id)
    {
        using var db = factory.CreateDbContext();
        return db.Deliveries.AsNoTracking().FirstOrDefault(d => d.Id == id && d.SandboxId == sandboxId);
    }

    public IReadOnlyList<WebhookAttempt> Attempts(string deliveryId)
    {
        using var db = factory.CreateDbContext();
        return db.Attempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).OrderBy(a => a.Number).ToList();
    }

    public bool Resend(string sandboxId, string id, string? url, DateTimeOffset utcNow)
    {
        using var db = factory.CreateDbContext();
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE webhook_deliveries
            SET status = 'Pending', max_attempts = attempts + 1, next_attempt_at = {utcNow}, lease_token = NULL,
                url = COALESCE({url}, url)
            WHERE id = {id} AND sandbox_id = {sandboxId} AND status IN ('Delivered', 'DeadLettered')
            """) == 1;
    }

    public void DeleteSandbox(string sandboxId)
    {
        using var db = factory.CreateDbContext();
        var ids = db.Deliveries.Where(d => d.SandboxId == sandboxId).Select(d => d.Id);
        db.Attempts.Where(a => ids.Contains(a.DeliveryId)).ExecuteDelete();
        db.Deliveries.Where(d => d.SandboxId == sandboxId).ExecuteDelete();
    }

    public int DeleteCreatedBefore(DateTimeOffset before)
    {
        using var db = factory.CreateDbContext();
        var ids = db.Deliveries.Where(d => d.CreatedAt < before).Select(d => d.Id);
        db.Attempts.Where(a => ids.Contains(a.DeliveryId)).ExecuteDelete();
        return db.Deliveries.Where(d => d.CreatedAt < before).ExecuteDelete();
    }
}

/// One transaction per request: entities (version-checked), sandbox clock, journal and webhook
/// deliveries land together or not at all.
public sealed class PostgresChangeCommitter(IDbContextFactory<ChameleonDbContext> factory) : IChangeCommitter
{
    public void Commit(ChangeSet changes)
    {
        using var db = factory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();
        Sql.WriteEntities(db, changes.SandboxId, changes.Entities);
        if (changes.Sandbox is not null) Sql.UpsertSandbox(db, changes.Sandbox);
        db.Interactions.AddRange(changes.Journal);
        db.Deliveries.AddRange(changes.Deliveries);
        db.SaveChanges();
        tx.Commit();
    }
}

public sealed class PostgresAccessTokenStore(IDbContextFactory<ChameleonDbContext> factory) : IAccessTokenStore
{
    public void Add(AccessToken token)
    {
        using var db = factory.CreateDbContext();
        db.AccessTokens.Add(token);
        db.SaveChanges();
    }

    public AccessToken? FindByHash(string tokenHash)
    {
        using var db = factory.CreateDbContext();
        return db.AccessTokens.AsNoTracking().SingleOrDefault(t => t.TokenHash == tokenHash);
    }

    public IReadOnlyList<AccessToken> List(string ownerEmail)
    {
        using var db = factory.CreateDbContext();
        return db.AccessTokens.AsNoTracking().Where(t => t.OwnerEmail == ownerEmail).OrderBy(t => t.CreatedAt).ToList();
    }

    public bool Revoke(string ownerEmail, Guid id, DateTimeOffset utcNow)
    {
        using var db = factory.CreateDbContext();
        if (!db.AccessTokens.Any(t => t.Id == id && t.OwnerEmail == ownerEmail)) return false;
        db.AccessTokens.Where(t => t.Id == id && t.RevokedAt == null)
            .ExecuteUpdate(u => u.SetProperty(t => t.RevokedAt, utcNow));
        return true;
    }

    public void Touch(Guid id, DateTimeOffset utcNow)
    {
        using var db = factory.CreateDbContext();
        db.AccessTokens.Where(t => t.Id == id).ExecuteUpdate(u => u.SetProperty(t => t.LastUsedAt, utcNow));
    }
}
