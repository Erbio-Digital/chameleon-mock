using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using Chameleon.Host;
using Chameleon.Storage.Postgres;
using Testcontainers.PostgreSql;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// One real Postgres (Testcontainers) for the class; migrations applied once, like CHAMELEON_MIGRATE_ON_START.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();

    public string Connection => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var sp = new ServiceCollection().AddPostgresStorage(Connection).BuildServiceProvider();
        sp.MigratePostgres();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public ChameleonFactory Factory() => new(s => s.AddPostgresStorage(Connection));

    public ChameleonDbContext Db() => new(new DbContextOptionsBuilder<ChameleonDbContext>().UseNpgsql(Connection).Options);
}

/// Everything lives in Postgres, one transaction per request.
public sealed class PostgresStorageTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact] // Personal tokens by hash, listed and revoked by their owner only
    public void Access_tokens_live_in_postgres()
    {
        using var sp = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider();
        var tokens = sp.GetRequiredService<IAccessTokenStore>();
        var (value, hash) = AccessToken.Generate();
        var token = new AccessToken { OwnerEmail = "ana@example.com", Name = "notebook", TokenHash = hash };
        tokens.Add(token);

        Assert.Equal(token.Id, tokens.FindByHash(AccessToken.Hash(value))!.Id);
        tokens.Touch(token.Id, DateTimeOffset.UtcNow);
        Assert.NotNull(Assert.Single(tokens.List("ana@example.com")).LastUsedAt);
        Assert.False(tokens.Revoke("bia@example.com", token.Id, DateTimeOffset.UtcNow));
        Assert.True(tokens.Revoke("ana@example.com", token.Id, DateTimeOffset.UtcNow));
        Assert.NotNull(tokens.FindByHash(hash)!.RevokedAt);
    }

    [Fact]
    public void Lease_by_id_takes_a_due_delivery_only_once()
    {
        using var sp = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider();
        var outbox = sp.GetRequiredService<IWebhookOutbox>();
        var now = DateTimeOffset.UtcNow;
        WebhookDelivery New(DateTimeOffset at) => new()
        {
            SandboxId = "lease-by-id", Connector = "bradesco", EntityId = "e", Url = "https://app", Payload = "{}",
            NextAttemptAt = at, MaxAttempts = 1,
        };
        var due = New(now.AddSeconds(-1));
        var early = New(now.AddMinutes(5));
        outbox.Enqueue([due, early]);

        Assert.Null(outbox.LeaseById("lease-by-id", early.Id, now, TimeSpan.FromMinutes(1)));
        Assert.Null(outbox.LeaseById("outro", due.Id, now, TimeSpan.FromMinutes(1)));
        var leased = outbox.LeaseById("lease-by-id", due.Id, now, TimeSpan.FromMinutes(1));
        Assert.Equal(DeliveryStatus.Delivering, leased!.Status);
        Assert.NotNull(leased.LeaseToken);
        Assert.Null(outbox.LeaseById("lease-by-id", due.Id, now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Data_survives_restart()
    {
        var txid = NewTxId();
        var sandbox = "pg-restart-" + Guid.NewGuid().ToString("N")[..8];
        string? token;
        int journalBefore;
        string clockBefore;

        using (var first = pg.Factory())
        {
            var http = await AuthorizedClient(first);
            token = http.DefaultRequestHeaders.Authorization!.Parameter;
            Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, txid)).StatusCode);
            (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();
            await first.SettledAsync();
            journalBefore = (await Verify(http, new { entityId = txid })).GetProperty("count").GetInt32();

            (await http.PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
            (await http.PostAsJsonAsync($"/s/{sandbox}/_chameleon/clock/advance", new { seconds = 86400 })).EnsureSuccessStatusCode();
            clockBefore = (await http.GetFromJsonAsync<JsonElement>($"/_chameleon/sandboxes/{sandbox}")).GetProperty("now").GetString()!;
        }

        using var second = pg.Factory();
        var again = second.CreateClient();
        again.DefaultRequestHeaders.Authorization = new("Bearer", token);

        Assert.Equal(journalBefore, (await Verify(again, new { entityId = txid })).GetProperty("count").GetInt32());
        var cob = await again.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.Equal("CONCLUIDA", cob.GetProperty("status").GetString());
        var clockAfter = (await again.GetFromJsonAsync<JsonElement>($"/_chameleon/sandboxes/{sandbox}")).GetProperty("now").GetString();
        Assert.Equal(clockBefore, clockAfter);
    }

    [Fact]
    public void Failed_commit_leaves_nothing()
    {
        using var sp = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider();
        var committer = sp.GetRequiredService<IChangeCommitter>();
        var key = "atomic-" + Guid.NewGuid().ToString("N")[..8];

        // Entity and webhook delivery are written, then the journal insert violates NOT NULL:
        // all of it rolls back.
        var broken = new Interaction(InteractionKind.StateTransition, "bradesco", key, DateTimeOffset.UtcNow, null!);
        Assert.ThrowsAny<Exception>(() => committer.Commit(new ChangeSet(Sandbox.Default, null,
            [new EntityChange(typeof(OAuthProbe), key, new OAuthProbe { Key = key })], [broken], [Delivery(key)])));

        Assert.Null(sp.GetRequiredService<IEntityStore>().Get(Sandbox.Default, typeof(OAuthProbe), key));
        Assert.Empty(sp.GetRequiredService<IWebhookOutbox>().Find(Sandbox.Default, entityId: key));
    }

    [Fact]
    public void Stale_write_conflicts()
    {
        using var factory = pg.Factory();
        TickerAndConcurrencyTests.StaleWriteConflicts(factory.Services);
    }

    [Fact]
    public void Lease_is_exclusive_and_survives_restart()
    {
        var key = "lease-" + Guid.NewGuid().ToString("N")[..8];
        using (var sp = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider())
        {
            var queued = Delivery(key);
            queued.Attempts = 1;
            sp.GetRequiredService<IWebhookOutbox>().Enqueue([queued]);
        }

        using var one = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider();
        using var two = new ServiceCollection().AddPostgresStorage(pg.Connection).BuildServiceProvider();
        var now = DateTimeOffset.UtcNow;
        var leases = new[] { one, two }.AsParallel()
            .SelectMany(sp => sp.GetRequiredService<IWebhookOutbox>().Lease(now, TimeSpan.FromMilliseconds(1), 50))
            .Where(d => d.EntityId == key)
            .ToList();
        var leased = Assert.Single(leases);
        Assert.Equal(1, leased.Attempts);

        // The lease lapses (worker died): another instance takes it with a new token; the old one can no longer record.
        var retaken = two.GetRequiredService<IWebhookOutbox>().Lease(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(1), 50)
            .Single(d => d.EntityId == key);
        Assert.NotEqual(leased.LeaseToken, retaken.LeaseToken);

        var journal = new Interaction(InteractionKind.OutboundWebhook, "bradesco", key, DateTimeOffset.UtcNow, "webhook");
        leased.Attempts++;
        leased.Status = DeliveryStatus.Delivered;
        Assert.False(one.GetRequiredService<IWebhookOutbox>().Complete(leased, Attempt(leased), journal));

        retaken.Attempts++;
        retaken.Status = DeliveryStatus.Delivered;
        Assert.True(two.GetRequiredService<IWebhookOutbox>().Complete(retaken, Attempt(retaken), journal));
        Assert.Single(two.GetRequiredService<IWebhookOutbox>().Attempts(retaken.Id));
        Assert.Equal(2, two.GetRequiredService<IWebhookOutbox>().Get(Sandbox.Default, retaken.Id)!.Attempts);
    }

    [Fact]
    public async Task Retries_until_delivered_on_postgres()
    {
        using var factory = pg.Factory();
        factory.Webhooks.Respond = n => n == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK;
        var sandbox = "pg-retry-" + Guid.NewGuid().ToString("N")[..8];
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        (await control.PutAsJsonAsync($"/_chameleon/sandboxes/{sandbox}/webhook-policy", new { maxAttempts = 3, baseDelayMs = 50, timeoutMs = 2000 }))
            .EnsureSuccessStatusCode();

        var http = await SandboxClient(factory, sandbox);
        var txid = NewTxId();
        await CreateCob(http, txid);
        (await http.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();
        await factory.SettledAsync(sandbox);

        var outbox = factory.Services.GetRequiredService<IWebhookOutbox>();
        var delivery = Assert.Single(outbox.Find(sandbox, txid));
        Assert.Equal((DeliveryStatus.Delivered, 2), (delivery.Status, delivery.Attempts));
        Assert.Equal([502, 200], outbox.Attempts(delivery.Id).Select(a => a.StatusCode ?? 0));
    }

    [Fact]
    public async Task Feed_cursor_and_fault_list_on_postgres()
    {
        using var factory = pg.Factory();
        var sandbox = "pg-feed-" + Guid.NewGuid().ToString("N")[..8];
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        var http = await SandboxClient(factory, sandbox);
        var txid = NewTxId();
        await CreateCob(http, txid);
        var pausedAt = (await http.GetFromJsonAsync<JsonElement>("_chameleon/interactions?limit=1")).GetProperty("items")[0].GetProperty("seq").GetInt64();
        for (var i = 0; i < 5; i++) await http.GetAsync($"bradesco/v2/cob/{txid}?n={i}");

        var resumed = (await http.GetFromJsonAsync<JsonElement>($"_chameleon/interactions?afterSeq={pausedAt}&limit=500"))
            .GetProperty("items").EnumerateArray().Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"/bradesco/v2/cob/{txid}?n={i}"), resumed);

        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "bradesco", route = "webhook", kind = "Duplicate", times = 3 }))
            .EnsureSuccessStatusCode();
        var armed = Assert.Single((await http.GetFromJsonAsync<JsonElement>("_chameleon/faults")).EnumerateArray().ToList());
        Assert.Equal(("webhook", 3), (armed.GetProperty("route").GetString(), armed.GetProperty("remaining").GetInt32()));
    }

    private static WebhookDelivery Delivery(string key) => new()
    {
        SandboxId = Sandbox.Default, Connector = "bradesco", EntityId = key, Url = "https://app.local/hook",
        Payload = "{}", MaxAttempts = 3, BaseDelayMs = 0, TimeoutMs = 1000,
    };

    private static WebhookAttempt Attempt(WebhookDelivery d) =>
        new(d.Id, d.Attempts, DateTimeOffset.UtcNow, d.Url, 200, 1, "ok", null, false);

    [Fact]
    public async Task Reset_clears_only_that_sandbox()
    {
        using var factory = pg.Factory();
        var control = factory.CreateClient();
        var (a, b) = ("pg-a-" + Guid.NewGuid().ToString("N")[..8], "pg-b-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var id in new[] { a, b })
            (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id })).EnsureSuccessStatusCode();

        var txid = NewTxId();
        foreach (var id in new[] { a, b })
            Assert.Equal(HttpStatusCode.Created, (await CreateCob(await SandboxClient(factory, id), txid)).StatusCode);

        (await control.PostAsync($"/_chameleon/sandboxes/{a}/reset", null)).EnsureSuccessStatusCode();

        // Reset drops the subscription too: unsubscribed again, then a fresh one sees no charge.
        Assert.Equal(HttpStatusCode.NotFound,
            (await (await SandboxClient(factory, a, token: false)).GetAsync($"bradesco/v2/cob/{txid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await (await SandboxClient(factory, a)).GetAsync($"bradesco/v2/cob/{txid}")).StatusCode);
        var inB = await (await SandboxClient(factory, b)).GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}");
        Assert.Equal("ATIVA", inB.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Copy_sandbox_on_postgres()
    {
        using var factory = pg.Factory();
        var control = factory.CreateClient();
        var (src, copy) = ("pg-src-" + Guid.NewGuid().ToString("N")[..8], "pg-cp-" + Guid.NewGuid().ToString("N")[..8]);
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = src })).EnsureSuccessStatusCode();
        var txid = NewTxId();
        await CreateCob(await SandboxClient(factory, src), txid);

        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = copy, from = src })).EnsureSuccessStatusCode();

        var list = await control.GetFromJsonAsync<JsonElement>($"/s/{copy}/_chameleon/connectors/bradesco/entities/cob");
        var cob = Assert.Single(list.EnumerateArray().ToList());
        Assert.Equal((txid, 1L), (cob.GetProperty("key").GetString(), cob.GetProperty("version").GetInt64()));
        Assert.NotEqual(JsonValueKind.Null, cob.GetProperty("dueAt").ValueKind);
    }

    [Fact]
    public async Task Fault_is_consumed_once_across_instances()
    {
        using var one = pg.Factory();
        using var two = pg.Factory();
        var sandbox = "pg-fault-" + Guid.NewGuid().ToString("N")[..8];
        (await one.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        (await one.CreateClient().PostAsJsonAsync($"/s/{sandbox}/_chameleon/faults",
                new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Error", status = 503, times = 5 }))
            .EnsureSuccessStatusCode();

        var clients = new[] { await SandboxClient(one, sandbox), await SandboxClient(two, sandbox) };
        var statuses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => CreateCob(clients[i % 2], NewTxId()).ContinueWith(t => t.Result.StatusCode)));

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.ServiceUnavailable));
        Assert.Equal(15, statuses.Count(s => s == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Timed_fault_on_postgres_does_not_count_uses_and_ends_with_the_clock()
    {
        using var factory = pg.Factory();
        var sandbox = "pg-timed-" + Guid.NewGuid().ToString("N")[..8];
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        var http = await SandboxClient(factory, sandbox);
        (await http.PostAsJsonAsync("_chameleon/faults",
                new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Error", status = 503, durationSeconds = 300 }))
            .EnsureSuccessStatusCode();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(http, NewTxId())).StatusCode);
        await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 300 });
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, NewTxId())).StatusCode);
    }

    [Fact]
    public async Task Retention_purges_old_history_and_expired_sandboxes()
    {
        using var factory = pg.Factory().WithPersistent("qa");
        var http = factory.CreateClient();
        var ephemeral = "pg-ttl-" + Guid.NewGuid().ToString("N")[..8];
        (await http.PostAsJsonAsync("/_chameleon/sandboxes", new { id = ephemeral, ttlSeconds = 60 })).EnsureSuccessStatusCode();
        var txid = NewTxId();
        await CreateCob(await AuthorizedClient(factory), txid);

        // Two days later with a one-day retention: the journal is old, the ephemeral sandbox expired.
        var (_, removed) = RetentionService.Purge(factory.Services, DateTimeOffset.UtcNow.AddDays(2), retentionDays: 1);

        Assert.True(removed >= 1);
        Assert.Equal(0, (await Verify(http, new { entityId = txid })).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/_chameleon/sandboxes/{ephemeral}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/_chameleon/sandboxes/qa")).StatusCode);
    }

    [Fact]
    public async Task Masked_payload_is_what_is_stored()
    {
        using var factory = pg.Factory();
        var txid = NewTxId();
        await CreateCob(await AuthorizedClient(factory), txid);

        await using var db = pg.Db();
        var stored = await db.Interactions.AsNoTracking()
            .Where(i => i.EntityId == txid && i.Kind == InteractionKind.InboundRequest)
            .Select(i => new { i.RequestBody, i.RequestHeaders })
            .SingleAsync();
        Assert.DoesNotContain("12345678909", stored.RequestBody);
        Assert.DoesNotContain("Joao Silva", stored.RequestBody);
        Assert.Equal("[redacted]", stored.RequestHeaders!["Authorization"]);
    }

    [Fact]
    public async Task Hold_on_postgres_retains_delivery_until_disarmed_and_is_never_consumed()
    {
        using var factory = pg.Factory();
        var sandbox = "pg-hold-" + Guid.NewGuid().ToString("N")[..8];
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = sandbox })).EnsureSuccessStatusCode();
        var http = await SandboxClient(factory, sandbox);
        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "bradesco", route = "webhook", kind = "Hold" })).EnsureSuccessStatusCode();

        var txid = NewTxId();
        await CreateCob(http, txid);
        (await http.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();
        await Task.Delay(1500);

        var outbox = factory.Services.GetRequiredService<IWebhookOutbox>();
        Assert.Equal((DeliveryStatus.Pending, 0), (outbox.Find(sandbox, txid)[0].Status, outbox.Find(sandbox, txid)[0].Attempts));
        Assert.Contains(factory.Services.GetRequiredService<IFaultStore>().List(sandbox), f => f.Fault.Kind == FaultKind.Hold);

        (await http.DeleteAsync("_chameleon/faults?connector=bradesco&route=webhook")).EnsureSuccessStatusCode();
        await factory.SettledAsync(sandbox);
        Assert.Equal(DeliveryStatus.Delivered, outbox.Find(sandbox, txid)[0].Status);
    }

    private static async Task<HttpClient> SandboxClient(ChameleonFactory factory, string sandbox, bool token = true)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        if (!token) return http;

        await TestAuth.Bradesco(http, sandbox);
        return http;
    }

    public sealed class OAuthProbe : IHasNaturalKey
    {
        public required string Key { get; init; }
        public string NaturalKeyProperty => nameof(Key);
    }
}
