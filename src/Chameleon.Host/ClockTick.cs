using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Host;

/// Applies what is due in the current sandbox: every connector's clock hook, in the caller's scope.
/// Shared by the advance endpoint and the realtime ticker.
public static class ClockTick
{
    public static async Task RunAsync(IServiceProvider scoped)
    {
        scoped.GetRequiredService<RequestScope>().ByClock = true;
        var ctx = scoped.GetRequiredService<ConnectorContext>();
        foreach (var connector in scoped.GetServices<IConnector>())
        {
            ctx.Connector = connector.Name;
            await connector.OnClockAdvancedAsync(ctx);
        }
    }
}

/// Realtime sandboxes expire things on their own: once a second, each realtime
/// sandbox with something due runs a tick in its own scope. Frozen sandboxes only move by advance.
public sealed class ClockTicker(IServiceProvider services, ILogger<ClockTicker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAllAsync(services, DateTimeOffset.UtcNow, logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ticker failed; retrying in {Interval}", Interval);
            }
        }
    }

    /// One sandbox failing (a connector bug, a broken entity) does not stop the others' ticks.
    public static async Task TickAllAsync(IServiceProvider services, DateTimeOffset utcNow, ILogger? logger = null)
    {
        var entities = services.GetRequiredService<IEntityStore>();
        var due = services.GetRequiredService<ISandboxStore>().All()
            .Where(s => s.ClockMode == ClockMode.Realtime && !s.IsExpired(utcNow) && entities.AnyDue(s.Id, s.Now(utcNow)));

        foreach (var sandbox in due)
        {
            using var scope = services.CreateScope();
            try
            {
                scope.ServiceProvider.GetRequiredService<SandboxContext>().Use(sandbox);
                await ClockTick.RunAsync(scope.ServiceProvider);
                scope.ServiceProvider.GetRequiredService<RequestScope>().Commit();
            }
            catch (ConcurrencyConflictException)
            {
                // A request moved the entity first; the next tick re-evaluates it.
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Tick of sandbox {SandboxId} failed; the others go on", sandbox.Id);
            }
        }
    }
}

/// Delivers queued webhooks with retries: leases due deliveries, posts them,
/// and records each attempt with the delivery's new state. Only the lease holder can record.
public sealed class WebhookWorker(IServiceProvider services, ILogger<WebhookWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(250);
    // Above the largest attempt timeout (30 s): a lease only expires if the worker died mid-attempt.
    private static readonly TimeSpan LeaseFor = TimeSpan.FromSeconds(60);
    // A held delivery is looked at again this often, so disarming releases it within a second.
    private static readonly TimeSpan HeldRecheck = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                delivered = await DeliverDueAsync(services, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Webhook delivery failed; retrying in {Idle}", Idle);
            }
            // ponytail: polling every 250 ms; signal after commit if latency ever matters.
            if (delivered == 0) await Task.Delay(Idle, stoppingToken);
        }
    }

    public static async Task<int> DeliverDueAsync(IServiceProvider services, CancellationToken ct)
    {
        var outbox = services.GetRequiredService<IWebhookOutbox>();
        var leased = outbox.Lease(DateTimeOffset.UtcNow, LeaseFor, max: 20);
        await Task.WhenAll(leased.Select(d => DeliverAsync(services, outbox, d, ct)));
        return leased.Count;
    }

    /// The queue names one delivery: it goes out only if it is due and nobody holds it; an
    /// early or taken one is left to its holder or to the sweep. True when this call attempted it.
    public static async Task<bool> DeliverByIdAsync(IServiceProvider services, string sandboxId, string deliveryId, CancellationToken ct)
    {
        var outbox = services.GetRequiredService<IWebhookOutbox>();
        if (outbox.LeaseById(sandboxId, deliveryId, DateTimeOffset.UtcNow, LeaseFor) is not { } leased) return false;
        await DeliverAsync(services, outbox, leased, ct);
        return true;
    }

    private static async Task DeliverAsync(IServiceProvider services, IWebhookOutbox outbox, WebhookDelivery d, CancellationToken ct)
    {
        var sandbox = services.GetRequiredService<ISandboxStore>().Get(d.SandboxId);
        if (services.GetRequiredService<IFaultStore>().IsHeld(d.SandboxId, d.Connector, sandbox?.Now(DateTimeOffset.UtcNow) ?? DateTimeOffset.UtcNow))
        {
            // Retained, no attempt and no journal entry until the Hold is disarmed.
            outbox.Postpone(d, DateTimeOffset.UtcNow + HeldRecheck);
            return;
        }

        var descriptor = services.GetServices<IConnector>().FirstOrDefault(c => c.Name == d.Connector)?.Describe();
        // A connector whose real provider calls webhooks with mTLS presents its certificate (TLS only).
        var certificate = d.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && descriptor?.WebhookCertificate == true
            ? services.GetRequiredService<CertificateAuthority>().WebhookClient(d.Connector)
            : null;
        var dispatcher = services.GetRequiredService<IWebhookDispatcher>();
        // Read at send time: a header set after the webhook was queued (or a retry) already goes out.
        var headers = (sandbox?.WebhookHeadersFor(d.Url) ?? []).ToList();
        if (descriptor?.WebhookSigner is { } signer)
        {
            var subscription = services.GetRequiredService<IEntityStore>().Get(d.SandboxId, typeof(ConnectorSubscription), d.Connector)?.Entity
                as ConnectorSubscription;
            var secrets = services.GetRequiredService<SecretBox>();
            var host = Uri.TryCreate(d.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
            headers.AddRange(signer(new WebhookSigningInput(d.Payload, DateTimeOffset.UtcNow, d.Note,
                    env => subscription is { Active: true } ? subscription.Value(descriptor, secrets, env) : null))
                .Select(h => new WebhookHeader(host, h.Key, h.Value)));
        }
        var result = await dispatcher.SendAsync(d.Url, d.Payload, TimeSpan.FromMilliseconds(d.TimeoutMs), ct, certificate, headers);
        var utcNow = DateTimeOffset.UtcNow;

        d.Attempts++;
        var ok = result.StatusCode is >= 200 and < 300;
        d.Status = ok ? DeliveryStatus.Delivered
            : result.Blocked || d.Attempts >= d.MaxAttempts ? DeliveryStatus.DeadLettered
            : DeliveryStatus.Pending;
        if (d.Status == DeliveryStatus.Pending)
            d.NextAttemptAt = utcNow.AddMilliseconds(d.BaseDelayMs * Math.Pow(2, d.Attempts - 1));
        d.LeaseUntil = null;

        var outcome = result.Blocked ? $"blocked: {result.Error}"
            : result.StatusCode is { } code ? $"{code}"
            : $"falhou: {result.Error}";
        if (certificate is not null) outcome += " (with the provider certificate)";
        var request = PayloadSanitizer.Body(d.Payload);
        var response = PayloadSanitizer.Body(result.ResponseBody);
        var sandboxNow = sandbox?.Now(utcNow) ?? utcNow;

        var attempt = new WebhookAttempt(d.Id, d.Attempts, utcNow, d.Url, result.StatusCode, result.DurationMs,
            response.Body, result.Error, result.Blocked);
        var journal = new Interaction(InteractionKind.OutboundWebhook, d.Connector, d.EntityId, sandboxNow,
            $"webhook -> {PayloadSanitizer.Url(d.Url)}",
            $"tentativa {d.Attempts}/{d.MaxAttempts}: {outcome}" + (d.Note is null ? "" : $" ({d.Note})"))
        {
            SandboxId = d.SandboxId,
            CorrelationId = d.CorrelationId,
            Method = "POST",
            Path = PayloadSanitizer.Url(d.Url),
            RequestHeaders = headers.Count == 0 ? null : headers.ToDictionary(h => h.Name, _ => PayloadSanitizer.Redacted),
            StatusCode = result.StatusCode,
            DurationMs = result.DurationMs,
            RequestBody = request.Body,
            ResponseBody = response.Body,
            Truncated = request.Truncated || response.Truncated,
            RecordedAt = utcNow,
        };

        // False: the lease expired and someone else took it; their attempt is the one that counts.
        if (!outbox.Complete(d, attempt, journal)) return;
        services.GetRequiredService<ILiveEventBus>().Publish(d.SandboxId,
            [new LiveEvent(LiveEvent.Interaction, journal), new LiveEvent(LiveEvent.WebhookUpdated, LiveEvents.Webhook(d))]);
        if (d.Status == DeliveryStatus.Pending) services.GetRequiredService<IWebhookSignal>().Signal(d);
    }
}
