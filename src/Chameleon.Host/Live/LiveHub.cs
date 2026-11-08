using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Chameleon.Engine;

namespace Chameleon.Host.Live;

/// The panel's live channel: a client joins the sandbox it is watching and receives its events.
public sealed class LiveHub(ISandboxStore sandboxes) : Hub
{
    public const string Path = "/_chameleon/hub";

    public static string Group(string sandboxId) => $"sandbox:{sandboxId}";

    public async Task Join(string sandboxId)
    {
        if (!Sandbox.IsValidId(sandboxId) || sandboxes.Get(sandboxId) is null)
            throw new HubException($"sandbox not found: {sandboxId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(sandboxId));
    }

    public Task Leave(string sandboxId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(sandboxId));
}

/// Buffers events per sandbox and sends them in batches every 250 ms, at most 500 per batch:
/// a CI burst never floods the panel; what is cut becomes an overflow and the panel reloads
/// through the API (the journal cursor has everything).
public sealed class SignalRLiveEventBus(IHubContext<LiveHub> hub, ILogger<SignalRLiveEventBus> logger)
    : BackgroundService, ILiveEventBus
{
    public const int MaxPerBatch = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LiveEvent>> _pending = new();

    public void Publish(string sandboxId, IReadOnlyCollection<LiveEvent> events)
    {
        if (events.Count == 0) return;
        var queue = _pending.GetOrAdd(sandboxId, _ => new ConcurrentQueue<LiveEvent>());
        foreach (var e in events) queue.Enqueue(e);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await FlushAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sending live events failed");
            }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        foreach (var (sandboxId, queue) in _pending)
        {
            if (queue.IsEmpty) continue;

            var batch = new List<LiveEvent>(Math.Min(queue.Count, MaxPerBatch + 1));
            while (batch.Count < MaxPerBatch && queue.TryDequeue(out var e)) batch.Add(e);
            var dropped = 0;
            while (queue.TryDequeue(out _)) dropped++;
            if (dropped > 0) batch.Add(new LiveEvent(LiveEvent.Overflow, new { dropped }));

            await hub.Clients.Group(LiveHub.Group(sandboxId)).SendAsync("events", new
            {
                sandboxId,
                events = batch.Select(e => new { type = e.Type, data = e.Data }),
            }, ct);
        }
    }
}
