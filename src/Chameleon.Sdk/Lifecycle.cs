using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Chameleon.Engine;

namespace Chameleon.Sdk;

/// A move the lifecycle does not allow. The host answers 409 and discards the request's writes.
public sealed class TransitionConflictException(string message) : Exception(message);

/// The states of one entity type and the transitions between them, declared once by the
/// connector. Moves are validated and journaled by ConnectorContext; the descriptor and the
/// Actions are derived from it.
public sealed class Lifecycle<TEntity, TState>(
    string entityType,
    TState initial,
    Func<TEntity, string> key,
    Func<TEntity, TState> getState,
    Action<TEntity, TState> setState)
    where TEntity : class where TState : struct, Enum
{
    private readonly List<(TState From, TState To, string Trigger)> _transitions = new();

    public string EntityType => entityType;

    public Lifecycle<TEntity, TState> Allow(TState from, TState to, string trigger)
    {
        _transitions.Add((from, to, trigger));
        return this;
    }

    public bool CanMove(TState from, TState to) => _transitions.Any(t => t.From.Equals(from) && t.To.Equals(to));

    internal string KeyOf(TEntity entity) => key(entity);
    internal TState StateOf(TEntity entity) => getState(entity);
    internal void SetState(TEntity entity, TState state) => setState(entity, state);

    internal void EnsureCanMove(TState from, TState to)
    {
        if (!CanMove(from, to))
            throw new TransitionConflictException($"{entityType} in {from} cannot move to {to}");
    }

    internal Interaction TransitionRecord(string connector, string entityKey, TState? from, TState to, DateTimeOffset at, string? reason) =>
        new(InteractionKind.StateTransition, connector, entityKey, at, $"{from?.ToString() ?? "criada"} -> {to}", reason)
        {
            EntityType = entityType,
            FromState = from?.ToString(),
            ToState = to.ToString(),
        };

    /// An action: the transitions with this trigger say from which states it is allowed.
    /// The entity is loaded by key (404 if missing) before the handler runs.
    public ConnectorAction Action(string trigger, string label,
        Func<ConnectorContext, TEntity, JsonElement?, Task<IResult>> run,
        params ActionParameter[] parameters) =>
        new(trigger, entityType, label,
            _transitions.Where(t => t.Trigger == trigger).Select(t => t.From.ToString()).Distinct().ToList(),
            parameters,
            async (ctx, entityKey, args) => ctx.Store.Get<TEntity>(entityKey) is { } entity
                ? await run(ctx, entity, args)
                : Results.NotFound(new { message = $"{entityType} {entityKey} not found" }));

    public EntityDescriptor Describe(string keyName) => new(
        entityType,
        keyName,
        initial.ToString(),
        Enum.GetNames<TState>(),
        _transitions.Select(t => new TransitionDescriptor(t.From.ToString(), t.To.ToString(), t.Trigger)).ToList(),
        typeof(TEntity),
        e => getState((TEntity)e).ToString());
}
