using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;

namespace Chameleon.Storage.Postgres;

public static class PostgresStorageExtensions
{
    /// Swaps every in-memory store (sandboxes, entities, journal, faults, webhook outbox) and the committer for
    /// Postgres. Nothing stays in memory, so any number of instances can share the database.
    /// Call this AFTER AddChameleonEngine so it overrides the default registrations.
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services, string connectionString)
    {
        services.AddPooledDbContextFactory<ChameleonDbContext>(o => o.UseNpgsql(connectionString));

        services.AddSingleton<ISandboxStore, PostgresSandboxStore>();
        services.AddSingleton<IEntityStore, PostgresEntityStore>();
        services.AddSingleton<IInteractionStore, PostgresInteractionStore>();
        services.AddSingleton<IFaultStore, PostgresFaultStore>();
        services.AddSingleton<IWebhookOutbox, PostgresWebhookOutbox>();
        services.AddSingleton<IChangeCommitter, PostgresChangeCommitter>();
        services.AddSingleton<IAccessTokenStore, PostgresAccessTokenStore>();
        return services;
    }

    /// Applies pending migrations on start (CHAMELEON_MIGRATE_ON_START=true). Without it, the migrations are
    /// expected to be applied out of band, before the container starts.
    public static void MigratePostgres(this IServiceProvider services)
    {
        using var db = services.GetRequiredService<IDbContextFactory<ChameleonDbContext>>().CreateDbContext();
        db.Database.Migrate();
    }
}
