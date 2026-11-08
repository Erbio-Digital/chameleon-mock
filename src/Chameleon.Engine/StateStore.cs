using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Chameleon.Engine;

/// An entity that expires or moves on its own at some instant (charge TTL, NFS-e processing).
/// The ticker only looks at entities whose DueAt has passed; null = nothing pending.
public interface IHasDueAt
{
    DateTimeOffset? DueAt { get; }
}

/// Two writes raced on the same entity: the later one is refused.
public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed record StoredEntity(string Key, object Entity, long Version, DateTimeOffset UpdatedAt);

/// One entity write. Checked: the request read it, so the write requires the version it saw
/// (ExpectedVersion null = it did not exist). Unchecked: blind write (tokens, settings).
public sealed record EntityChange(Type Type, string Key, object Entity, bool Checked = false, long? ExpectedVersion = null);

/// Persistence port for entities (in-memory, Postgres). Connector-agnostic.
public interface IEntityStore
{
    StoredEntity? Get(string sandboxId, Type type, string id);
    IReadOnlyCollection<StoredEntity> All(string sandboxId, Type type);
    IReadOnlyCollection<StoredEntity> Due(string sandboxId, Type type, DateTimeOffset now);
    bool AnyDue(string sandboxId, DateTimeOffset now);

    /// Applies the writes all or nothing; throws ConcurrencyConflictException on a stale version.
    void Write(string sandboxId, IReadOnlyCollection<EntityChange> changes);
    void DeleteSandbox(string sandboxId);

    /// Retention: removes, in every sandbox, entities of this type last written before the instant.
    int DeleteUpdatedBefore(Type type, DateTimeOffset before);

    /// Copies every entity of one sandbox into another (empty) one, each starting at version 1.
    void CopySandbox(string fromSandboxId, string toSandboxId);
}

/// How entities are stored (camelCase, enums as text). The in-memory store keeps the same JSON
/// as Postgres, so a mutation before commit never leaks into the store.
public static class EntityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                // Metadata, not data: the key's name and the derived due instant.
                info =>
                {
                    foreach (var name in new[] { "naturalKeyProperty", "dueAt" })
                        if (info.Properties.FirstOrDefault(p => p.Name == name) is { } meta)
                            info.Properties.Remove(meta);
                },
            },
        },
    };

    public static string Serialize(object entity, Type type) => JsonSerializer.Serialize(entity, type, Options);

    public static object Deserialize(string data, Type type) =>
        JsonSerializer.Deserialize(data, type, Options) ?? throw new InvalidOperationException($"entidade {type.Name} vazia");

    // UTC: connectors may compute due dates in local time (BRT), and Postgres timestamptz takes only UTC.
    public static DateTimeOffset? DueAt(object entity) => (entity as IHasDueAt)?.DueAt?.ToUniversalTime();
}

public sealed class InMemoryEntityStore : IEntityStore
{
    private sealed record Row(string Data, long Version, DateTimeOffset? DueAt, DateTimeOffset UpdatedAt);

    private readonly Dictionary<(string Sandbox, Type Type, string Key), Row> _rows = new();
    private readonly object _gate = new();

    public StoredEntity? Get(string sandboxId, Type type, string id)
    {
        lock (_gate)
            return _rows.TryGetValue((sandboxId, type, id), out var row) ? Load(id, type, row) : null;
    }

    public IReadOnlyCollection<StoredEntity> All(string sandboxId, Type type) => Where(sandboxId, type, _ => true);

    public IReadOnlyCollection<StoredEntity> Due(string sandboxId, Type type, DateTimeOffset now) =>
        Where(sandboxId, type, r => r.DueAt <= now);

    public bool AnyDue(string sandboxId, DateTimeOffset now)
    {
        lock (_gate) return _rows.Any(kv => kv.Key.Sandbox == sandboxId && kv.Value.DueAt <= now);
    }

    public void Write(string sandboxId, IReadOnlyCollection<EntityChange> changes)
    {
        lock (_gate)
        {
            foreach (var c in changes.Where(c => c.Checked))
            {
                var current = _rows.TryGetValue((sandboxId, c.Type, c.Key), out var row) ? row.Version : (long?)null;
                if (current != c.ExpectedVersion)
                    throw new ConcurrencyConflictException($"{c.Type.Name} {c.Key} was changed by another operation");
            }
            foreach (var c in changes)
            {
                var version = _rows.TryGetValue((sandboxId, c.Type, c.Key), out var row) ? row.Version + 1 : 1;
                _rows[(sandboxId, c.Type, c.Key)] = new Row(EntityJson.Serialize(c.Entity, c.Type), version, EntityJson.DueAt(c.Entity), DateTimeOffset.UtcNow);
            }
        }
    }

    public void DeleteSandbox(string sandboxId)
    {
        lock (_gate)
            foreach (var key in _rows.Keys.Where(k => k.Sandbox == sandboxId).ToList())
                _rows.Remove(key);
    }

    public int DeleteUpdatedBefore(Type type, DateTimeOffset before)
    {
        lock (_gate)
        {
            var old = _rows.Where(r => r.Key.Type == type && r.Value.UpdatedAt < before).Select(r => r.Key).ToList();
            old.ForEach(k => _rows.Remove(k));
            return old.Count;
        }
    }

    public void CopySandbox(string fromSandboxId, string toSandboxId)
    {
        lock (_gate)
            foreach (var (key, row) in _rows.Where(kv => kv.Key.Sandbox == fromSandboxId).ToList())
                _rows[(toSandboxId, key.Type, key.Key)] = row with { Version = 1, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private IReadOnlyCollection<StoredEntity> Where(string sandboxId, Type type, Func<Row, bool> filter)
    {
        lock (_gate)
            return _rows.Where(kv => kv.Key.Sandbox == sandboxId && kv.Key.Type == type && filter(kv.Value))
                .Select(kv => Load(kv.Key.Key, type, kv.Value))
                .ToList();
    }

    private static StoredEntity Load(string key, Type type, Row row) => new(key, EntityJson.Deserialize(row.Data, type), row.Version, row.UpdatedAt);
}
