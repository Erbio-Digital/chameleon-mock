using System.Collections.Concurrent;

namespace Chameleon.Engine;

/// A fault the test arms against a specific route, to exercise the app's failure paths.
/// The host translates the kind into the HTTP response; the engine only decides if one fires.
/// Duplicate, Reorder and Hold only apply to webhook delivery (route "webhook"); Latency to both.
/// Hold is never consumed: it retains deliveries until disarmed or until its deadline.
public enum FaultKind { Timeout, Error, Latency, Malformed, Duplicate, Reorder, Hold }

/// Armed for a number of uses (Times) or, with Until, for every call until that instant of the
/// sandbox clock; a timed fault does not count uses.
public sealed record Fault(FaultKind Kind, int? Status = null, int? DelayMs = null, int Times = 1, DateTimeOffset? Until = null)
{
    public bool ActiveAt(DateTimeOffset now) => Until is { } until ? now < until : Times > 0;

    /// Uses left after this one, for the live event; null for a timed fault.
    public int? RemainingAfterUse => Until is null ? Times - 1 : null;
}

/// Connector-agnostic and per sandbox: route = "METHOD pattern" exactly as the connector maps
/// it (e.g. "PUT /bradesco/v2/cob/{txid}"), or "webhook" for the connector's notifications.
public interface IFaultStore
{
    public const string WebhookRoute = "webhook";

    /// The asynchronous settlement of outgoing operations; only Hold applies.
    public const string SettlementRoute = "settlement";

    /// Every provider route of the connector; a fault on the exact route wins. Never webhook or settlement.
    public const string AllRoutes = "*";

    void Arm(string sandboxId, string connector, string route, Fault fault);
    void Clear(string sandboxId, string connector, string route);
    void DeleteSandbox(string sandboxId);

    /// Armed faults, expired timed ones included: filter with Fault.ActiveAt.
    IReadOnlyList<ArmedFault> List(string sandboxId);

    /// If a fault is active for (sandbox, connector, route) at the sandbox's now, consumes one use
    /// (timed faults: none) and returns it. Hold is not consumed here (see IsHeld).
    bool TryConsume(string sandboxId, string connector, string route, DateTimeOffset now, out Fault fault);
}

public sealed record ArmedFault(string Connector, string Route, Fault Fault);

public static class FaultStoreExtensions
{
    /// An armed Hold on the connector's route (webhook delivery or settlement) retains it.
    public static bool IsHeld(this IFaultStore faults, string sandboxId, string connector, DateTimeOffset now,
        string route = IFaultStore.WebhookRoute) =>
        faults.List(sandboxId).Any(f => f.Connector == connector && f.Route == route
                                        && f.Fault.Kind == FaultKind.Hold && f.Fault.ActiveAt(now));
}

public sealed class InMemoryFaultStore : IFaultStore
{
    private readonly ConcurrentDictionary<string, Fault> _armed = new();
    private readonly object _gate = new();
    private static string Key(string sandboxId, string connector, string route) => $"{sandboxId}|{connector}|{route}";

    public void Arm(string sandboxId, string connector, string route, Fault fault) => _armed[Key(sandboxId, connector, route)] = fault;
    public void Clear(string sandboxId, string connector, string route) => _armed.TryRemove(Key(sandboxId, connector, route), out _);

    public void DeleteSandbox(string sandboxId)
    {
        foreach (var key in _armed.Keys.Where(k => k.StartsWith(sandboxId + "|", StringComparison.Ordinal)))
            _armed.TryRemove(key, out _);
    }

    public IReadOnlyList<ArmedFault> List(string sandboxId) =>
        _armed.Where(kv => kv.Key.StartsWith(sandboxId + "|", StringComparison.Ordinal))
            .Select(kv => kv.Key.Split('|', 3) is [_, var connector, var route] ? new ArmedFault(connector, route, kv.Value) : null)
            .OfType<ArmedFault>()
            .OrderBy(f => f.Connector).ThenBy(f => f.Route)
            .ToList();

    public bool TryConsume(string sandboxId, string connector, string route, DateTimeOffset now, out Fault fault)
    {
        lock (_gate)
        {
            var key = Key(sandboxId, connector, route);
            if (!_armed.TryGetValue(key, out fault!) || fault.Kind == FaultKind.Hold) return false;
            if (fault.Until is { } until)
            {
                if (now < until) return true;
                _armed.TryRemove(key, out _);
                return false;
            }

            if (fault.Times <= 1) _armed.TryRemove(key, out _);
            else _armed[key] = fault with { Times = fault.Times - 1 };
            return true;
        }
    }
}
