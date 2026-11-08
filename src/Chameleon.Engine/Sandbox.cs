using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Chameleon.Engine;

public enum ClockMode { Realtime, Frozen }

/// An isolated copy of every provider: its own entities, journal, faults, credentials and clock.
public sealed partial class Sandbox : IHasNaturalKey
{
    public const string Default = "default";

    public required string Id { get; init; }
    public string NaturalKeyProperty => nameof(Id);

    /// Protected: never expires, reset only with explicit confirmation, never deleted (created as permanent).
    public bool Persistent { get; init; }
    public ClockMode ClockMode { get; set; } = ClockMode.Frozen;
    public double ClockOffsetSeconds { get; set; }
    public DateTimeOffset? FrozenAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }

    /// Overrides the connector's default webhook retry policy in this sandbox.
    public WebhookPolicy? WebhookPolicy { get; set; }

    /// Extra headers on webhooks to a destination host, for receivers that ask for a key (a request bin).
    /// Not a provider feature: the real provider sends none, so these never touch connector rules.
    public List<WebhookHeader> WebhookHeaders { get; set; } = [];

    /// The headers this sandbox adds to a webhook sent to the url.
    public IReadOnlyList<WebhookHeader> WebhookHeadersFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? WebhookHeaders.Where(h => string.Equals(h.Host, uri.Host, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    public DateTimeOffset Now(DateTimeOffset utcNow) =>
        ClockMode == ClockMode.Frozen ? FrozenAt ?? utcNow : utcNow.AddSeconds(ClockOffsetSeconds);

    public void Advance(TimeSpan by, DateTimeOffset utcNow)
    {
        if (ClockMode == ClockMode.Frozen) FrozenAt = Now(utcNow) + by;
        else ClockOffsetSeconds += by.TotalSeconds;
    }

    /// Freezing stops time at the current instant; resuming lets it run again from there.
    public void SetMode(ClockMode mode, DateTimeOffset utcNow)
    {
        if (mode == ClockMode) return;
        var now = Now(utcNow);
        ClockMode = mode;
        FrozenAt = mode == ClockMode.Frozen ? now : null;
        ClockOffsetSeconds = mode == ClockMode.Realtime ? (now - utcNow).TotalSeconds : 0;
    }

    public void ResetClock(DateTimeOffset utcNow)
    {
        ClockOffsetSeconds = 0;
        FrozenAt = ClockMode == ClockMode.Frozen ? utcNow : null;
    }

    /// A request works on its own copy: a clock change only lands when the request commits.
    public Sandbox Copy() => (Sandbox)MemberwiseClone();

    public bool IsExpired(DateTimeOffset utcNow) => ExpiresAt is { } at && utcNow >= at;

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();
}

/// Persistence port for sandboxes (in-memory, Postgres).
public interface ISandboxStore
{
    Sandbox? Get(string id);
    IReadOnlyList<Sandbox> All();
    void Put(Sandbox sandbox);
    void Delete(string id);
}

public sealed class InMemorySandboxStore : ISandboxStore
{
    private readonly ConcurrentDictionary<string, Sandbox> _sandboxes = new();

    public Sandbox? Get(string id) => _sandboxes.GetValueOrDefault(id);
    public IReadOnlyList<Sandbox> All() => _sandboxes.Values.OrderBy(s => s.CreatedAt).ToList();
    public void Put(Sandbox sandbox) => _sandboxes[sandbox.Id] = sandbox;
    public void Delete(string id) => _sandboxes.TryRemove(id, out _);
}

/// The sandbox the current request (or job) runs in. Set once by the host before anything
/// else reads it; the clock and every store write are scoped to it.
public sealed class SandboxContext
{
    private Sandbox? _sandbox;

    public Sandbox Sandbox => _sandbox ?? throw new InvalidOperationException("sandbox not resolved for this scope");
    public string Id => Sandbox.Id;

    /// True once the clock moved, so the scope persists the sandbox on commit.
    public bool Changed { get; set; }

    public void Use(Sandbox sandbox) => _sandbox = sandbox.Copy();
}
