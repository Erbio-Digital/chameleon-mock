using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chameleon.Engine;
using Microsoft.Extensions.DependencyInjection;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Webhook outbox, retries, resend, policy and webhook faults.
[Collection(Serial.Name)]
public sealed class WebhookOutboxTests
{
    [Fact]
    public async Task Delivered_on_first_attempt()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = await PaidCob(factory, http);

        var delivery = await Single(http, txid);
        Assert.Equal("Delivered", delivery.GetProperty("status").GetString());
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());

        Assert.Equal("pix", delivery.GetProperty("event").GetString());
        var detail = await Detail(http, delivery);
        Assert.Contains(txid, detail.GetProperty("payload").GetString());
        var attempt = detail.GetProperty("attempts")[0];
        Assert.Equal(200, attempt.GetProperty("statusCode").GetInt32());
        Assert.True(attempt.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal("ok", attempt.GetProperty("responseBody").GetString());
    }

    [Fact]
    public async Task Retries_with_growing_wait_until_delivered()
    {
        using var factory = new ChameleonFactory();
        factory.Webhooks.Respond = n => n < 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
        var http = await AuthorizedClient(factory);
        await SetPolicy(http, maxAttempts: 5, baseDelayMs: 150);

        var txid = await PaidCob(factory, http);

        var delivery = await Single(http, txid);
        Assert.Equal("Delivered", delivery.GetProperty("status").GetString());
        Assert.Equal(3, delivery.GetProperty("attempts").GetInt32());
        var at = factory.Webhooks.ReceivedAt.ToArray();
        Assert.True(at[1] - at[0] >= TimeSpan.FromMilliseconds(140), $"1a espera {at[1] - at[0]}");
        Assert.True(at[2] - at[1] >= TimeSpan.FromMilliseconds(290), $"2a espera {at[2] - at[1]}");

        var attempts = (await Detail(http, delivery)).GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal([500, 500, 200], attempts.Select(a => a.GetProperty("statusCode").GetInt32()));
    }

    [Fact]
    public async Task Exhausted_attempts_dead_letter_then_resend_to_another_url()
    {
        using var factory = new ChameleonFactory();
        factory.Webhooks.Respond = _ => HttpStatusCode.ServiceUnavailable;
        var http = await AuthorizedClient(factory);
        await SetPolicy(http, maxAttempts: 3, baseDelayMs: 10);

        var txid = await PaidCob(factory, http);
        var dead = await Single(http, txid);
        Assert.Equal("DeadLettered", dead.GetProperty("status").GetString());
        Assert.Equal(3, dead.GetProperty("attempts").GetInt32());
        await Task.Delay(300);
        Assert.Equal(3, factory.Webhooks.Received.Count);
        Assert.All((await Detail(http, dead)).GetProperty("attempts").EnumerateArray(),
            a => Assert.Equal("error", a.GetProperty("responseBody").GetString()));

        factory.Webhooks.Respond = _ => HttpStatusCode.OK;
        const string other = "https://outro-destino.local/webhooks/pix";
        var resend = await http.PostAsJsonAsync($"/_chameleon/webhooks/{dead.GetProperty("id").GetString()}/resend", new { url = other });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        await factory.SettledAsync();

        var after = await Single(http, txid);
        Assert.Equal("Delivered", after.GetProperty("status").GetString());
        var attempts = (await Detail(http, after)).GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal(4, attempts.Count);
        Assert.Equal(other, attempts[^1].GetProperty("url").GetString());
        Assert.Equal(other, factory.Webhooks.Received.Last().Url);
    }

    [Fact]
    public async Task Resend_validates_destination_and_state()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = await PaidCob(factory, http);
        var id = (await Single(http, txid)).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"/_chameleon/webhooks/{id}/resend", new { url = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/_chameleon/webhooks/does-not-exist/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync($"/_chameleon/webhooks/{id}/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/_chameleon/webhooks/{id}/resend", null)).StatusCode);
    }

    [Fact]
    public async Task Sandbox_policy_overrides_connector_default()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "pol-a" })).EnsureSuccessStatusCode();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "pol-b" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await control.PutAsJsonAsync("/_chameleon/sandboxes/pol-a/webhook-policy",
            new { maxAttempts = 0, baseDelayMs = 10, timeoutMs = 1000 })).StatusCode);
        (await control.PutAsJsonAsync("/_chameleon/sandboxes/pol-a/webhook-policy",
            new { maxAttempts = 2, baseDelayMs = 10, timeoutMs = 1000 })).EnsureSuccessStatusCode();
        factory.Webhooks.Respond = _ => HttpStatusCode.InternalServerError;

        foreach (var sandbox in new[] { "pol-a", "pol-b" })
        {
            var http = await SandboxClient(factory, sandbox);
            var txid = NewTxId();
            await CreateCob(http, txid);
            await http.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null);
        }

        await factory.SettledAsync("pol-a");
        var a = (await (await SandboxClient(factory, "pol-a")).GetFromJsonAsync<JsonElement>("_chameleon/webhooks"))[0];
        Assert.Equal(("DeadLettered", 2, 2), (a.GetProperty("status").GetString(), a.GetProperty("attempts").GetInt32(), a.GetProperty("maxAttempts").GetInt32()));

        // pol-b keeps the connector default (5 attempts doubling from 2 s): only check the plan.
        var b = (await (await SandboxClient(factory, "pol-b")).GetFromJsonAsync<JsonElement>("_chameleon/webhooks"))[0];
        Assert.Equal(5, b.GetProperty("maxAttempts").GetInt32());
        var connectors = await control.GetFromJsonAsync<JsonElement>("/_chameleon/connectors");
        var bradesco = connectors.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "bradesco");
        Assert.Equal(5, bradesco.GetProperty("webhooks").GetProperty("maxAttempts").GetInt32());
    }

    [Fact]
    public async Task Duplicate_fault_delivers_twice()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await ArmWebhookFault(http, "bradesco", "Duplicate");

        await PaidCob(factory, http);

        Assert.Equal(2, factory.Webhooks.Received.Count);
        var bodies = factory.Webhooks.Received.Select(r => r.Body).Distinct().ToList();
        Assert.Single(bodies);
    }

    [Fact]
    public async Task Reorder_fault_lets_later_notification_overtake()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await ArmWebhookFault(http, "bradesco", "Reorder", delayMs: 30000);

        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null);
        var rps = "RPS-" + Guid.NewGuid().ToString("N")[..8];
        (await http.PostAsJsonAsync("/nfse-sp/nfse", new { valor = 10, webhookUrl = "https://app-under-test.local/webhooks/nfse", rps })).EnsureSuccessStatusCode();
        await http.PostAsync($"/_chameleon/nfse-sp/nfse/{rps}/authorize", null);
        // The Pix notification was raised first but is held for 30 s: once the NFS-e one arrives it has overtaken it.
        await ChameleonFactory.Eventually(() => factory.Webhooks.Received.Count >= 1, timeoutMs: 30000);

        Assert.Equal(["https://app-under-test.local/webhooks/nfse"], factory.Webhooks.Received.Select(r => r.Url).ToList());
        var held = Assert.Single(factory.Services.GetRequiredService<IWebhookOutbox>().Find(Sandbox.Default), d => d.Url == WebhookUrl);
        Assert.Equal(DeliveryStatus.Pending, held.Status);
        Assert.Contains("out of order", (await Single(http, txid)).GetProperty("note").GetString());
    }

    [Fact]
    public async Task Latency_fault_delays_delivery()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await ArmWebhookFault(http, "bradesco", "Latency", delayMs: 500);

        var started = DateTimeOffset.UtcNow;
        var txid = await PaidCob(factory, http);

        Assert.True(factory.Webhooks.ReceivedAt.Single() - started >= TimeSpan.FromMilliseconds(490));
        Assert.Equal("armed delay of 500 ms", (await Single(http, txid)).GetProperty("note").GetString());
        var journal = (await Verify(http, new { entityId = txid, kind = "OutboundWebhook" })).GetProperty("matches")[0];
        Assert.Contains("armed delay of 500 ms", journal.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Webhook_fault_kinds_are_validated()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "webhook", kind = "Error" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Duplicate" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "ninguem", route = "webhook", kind = "Duplicate" })).StatusCode);
    }

    [Fact] // Refused transition queues no webhook
    public async Task Refused_transition_queues_nothing()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/remove", null);

        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).StatusCode);
        Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>($"/_chameleon/webhooks?entityId={txid}") ?? []);
    }

    private static async Task<string> PaidCob(ChameleonFactory factory, HttpClient http)
    {
        var txid = NewTxId();
        await CreateCob(http, txid);
        (await http.PostAsync($"/_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();
        await factory.SettledAsync();
        return txid;
    }

    private static async Task<JsonElement> Single(HttpClient http, string entityId) =>
        Assert.Single((await http.GetFromJsonAsync<JsonElement>($"/_chameleon/webhooks?entityId={entityId}")).EnumerateArray().ToList());

    private static Task<JsonElement> Detail(HttpClient http, JsonElement delivery) =>
        http.GetFromJsonAsync<JsonElement>($"/_chameleon/webhooks/{delivery.GetProperty("id").GetString()}");

    private static async Task SetPolicy(HttpClient http, int maxAttempts, int baseDelayMs) =>
        (await http.PutAsJsonAsync("/_chameleon/sandboxes/default/webhook-policy",
            new { maxAttempts, baseDelayMs, timeoutMs = 2000 })).EnsureSuccessStatusCode();

    private static async Task ArmWebhookFault(HttpClient http, string connector, string kind, int? delayMs = null) =>
        (await http.PostAsJsonAsync("/_chameleon/faults", new { connector, route = "webhook", kind, delayMs })).EnsureSuccessStatusCode();

    internal static async Task<HttpClient> SandboxClient(ChameleonFactory factory, string sandbox)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        await TestAuth.Bradesco(http, sandbox);
        return http;
    }
}
