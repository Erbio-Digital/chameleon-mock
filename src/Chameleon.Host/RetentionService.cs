using Chameleon.Sdk;
using Chameleon.Engine;
using Chameleon.Host.Http;

namespace Chameleon.Host;

/// Keeps the store small: journal, webhook deliveries and simulated OAuth tokens older than CHAMELEON_RETENTION_DAYS (wall clock)
/// are deleted and expired ephemeral sandboxes are removed with everything they hold. Persistent sandboxes never expire,
/// but their tokens do: every token request writes one, and a token lives an hour.
public sealed class RetentionService(IServiceProvider services, IConfiguration configuration, ILogger<RetentionService> logger) : BackgroundService
{
    public const int DefaultRetentionDays = 7;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                Purge(services, DateTimeOffset.UtcNow, RetentionDays(configuration));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retention failed; retrying in {Interval}", Interval);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static (int Interactions, int Sandboxes) Purge(IServiceProvider services, DateTimeOffset utcNow, int retentionDays)
    {
        var expired = services.GetRequiredService<ISandboxStore>().All().Where(s => !s.Persistent && s.IsExpired(utcNow)).ToList();
        foreach (var sandbox in expired)
            SandboxMiddleware.Remove(sandbox.Id, services);

        var before = utcNow.AddDays(-retentionDays);
        services.GetRequiredService<IWebhookOutbox>().DeleteCreatedBefore(before);
        services.GetRequiredService<IEntityStore>().DeleteUpdatedBefore(typeof(OAuthToken), before);
        return (services.GetRequiredService<IInteractionStore>().DeleteRecordedBefore(before), expired.Count);
    }

    public static int RetentionDays(IConfiguration configuration) =>
        int.TryParse(configuration["CHAMELEON_RETENTION_DAYS"], out var days) && days > 0 ? days : DefaultRetentionDays;
}
