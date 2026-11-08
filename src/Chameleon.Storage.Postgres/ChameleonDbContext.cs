using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Chameleon.Engine;

namespace Chameleon.Storage.Postgres;

/// A connector entity as stored: the schema never knows the connector, the entity is jsonb.
public sealed class EntityRow
{
    public required string SandboxId { get; init; }
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required string Data { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class FaultRow
{
    public required string SandboxId { get; init; }
    public required string Connector { get; init; }
    public required string Route { get; init; }
    public FaultKind Kind { get; set; }
    public int? Status { get; set; }
    public int? DelayMs { get; set; }
    public int Remaining { get; set; }
    public DateTimeOffset? Until { get; set; }
}

public sealed class ChameleonDbContext(DbContextOptions<ChameleonDbContext> options) : DbContext(options)
{
    public DbSet<Sandbox> Sandboxes => Set<Sandbox>();
    public DbSet<EntityRow> Entities => Set<EntityRow>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<FaultRow> Faults => Set<FaultRow>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();
    public DbSet<WebhookAttempt> Attempts => Set<WebhookAttempt>();
    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Sandbox>(b =>
        {
            b.ToTable("sandboxes");
            b.HasKey(s => s.Id);
            b.Ignore(s => s.NaturalKeyProperty);
            b.Property(s => s.ClockMode).HasConversion<string>();
            b.Property(s => s.WebhookPolicy)
                .HasColumnType("jsonb")
                .HasConversion(
                    p => p == null ? null : JsonSerializer.Serialize(p, (JsonSerializerOptions?)null),
                    s => s == null ? null : JsonSerializer.Deserialize<WebhookPolicy>(s, (JsonSerializerOptions?)null));
            b.Property(s => s.WebhookHeaders)
                .HasColumnType("jsonb")
                .HasConversion(
                    h => JsonSerializer.Serialize(h, (JsonSerializerOptions?)null),
                    s => JsonSerializer.Deserialize<List<WebhookHeader>>(s, (JsonSerializerOptions?)null) ?? new List<WebhookHeader>(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<WebhookHeader>>(
                        (a, b) => a!.SequenceEqual(b!), h => h.Aggregate(0, (x, y) => HashCode.Combine(x, y.GetHashCode())), h => h.ToList()))
                .HasDefaultValueSql("'[]'::jsonb");
        });

        model.Entity<EntityRow>(b =>
        {
            b.ToTable("entities");
            b.HasKey(e => new { e.SandboxId, e.Type, e.Key });
            b.Property(e => e.Data).HasColumnType("jsonb");
            b.HasIndex(e => new { e.SandboxId, e.DueAt });
        });

        model.Entity<Interaction>(b =>
        {
            b.ToTable("interactions");
            b.Property(i => i.Seq).UseIdentityAlwaysColumn();
            b.HasKey(i => i.Seq);
            b.Property(i => i.Kind).HasConversion<string>();
            b.Property(i => i.RequestHeaders)
                .HasColumnType("jsonb")
                .HasConversion(
                    h => h == null ? null : JsonSerializer.Serialize(h, (JsonSerializerOptions?)null),
                    s => s == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s, (JsonSerializerOptions?)null),
                    new ValueComparer<Dictionary<string, string>?>(
                        (a, b) => ReferenceEquals(a, b),
                        h => h == null ? 0 : h.Count,
                        h => h));
            b.HasIndex(i => new { i.SandboxId, i.At });
            b.HasIndex(i => new { i.SandboxId, i.EntityId });
            b.HasIndex(i => i.CorrelationId);
            b.HasIndex(i => i.RecordedAt);
        });

        model.Entity<FaultRow>(b =>
        {
            b.ToTable("faults");
            b.HasKey(f => new { f.SandboxId, f.Connector, f.Route });
            b.Property(f => f.Kind).HasConversion<string>();
        });

        model.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_deliveries");
            b.HasKey(d => d.Id);
            b.Property(d => d.Status).HasConversion<string>();
            b.Property(d => d.Payload).HasColumnType("jsonb");
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
            b.HasIndex(d => new { d.SandboxId, d.EntityId });
        });

        model.Entity<WebhookAttempt>(b =>
        {
            b.ToTable("webhook_attempts");
            b.Property<long>("Seq").UseIdentityAlwaysColumn();
            b.HasKey("Seq");
            b.HasIndex(a => a.DeliveryId);
        });

        model.Entity<AccessToken>(b =>
        {
            b.ToTable("access_tokens");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.OwnerEmail);
        });

        // snake_case columns, like the table names.
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(SnakeCase(property.Name));
    }

    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

/// Lets `dotnet ef migrations add` build the model without the host. The connection is only
/// used by `dotnet ef database update`; the app always gets it from CHAMELEON_POSTGRES_CONNECTION.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ChameleonDbContext>
{
    public ChameleonDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ChameleonDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("CHAMELEON_POSTGRES_CONNECTION")
                       ?? "Host=localhost;Database=chameleon")
            .Options);
}
