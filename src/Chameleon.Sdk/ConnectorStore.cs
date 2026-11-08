using Chameleon.Engine;

namespace Chameleon.Sdk;

/// What a connector reads and writes simulated entities through (e.g. a Cobranca), always inside
/// the current sandbox. A narrow view of the request scope: writes land when the request
/// completes, and only the host commits or discards them.
public sealed class ConnectorStore(RequestScope scope)
{
    public void Put<T>(string id, T entity) where T : class => scope.Put(id, entity);
    public T? Get<T>(string id) where T : class => scope.Get<T>(id);
    public IReadOnlyCollection<T> All<T>() where T : class => scope.All<T>();

    /// Entities of this type whose DueAt is at or before the given instant.
    public IReadOnlyCollection<T> Due<T>(DateTimeOffset now) where T : class => scope.Due<T>(now);
}
