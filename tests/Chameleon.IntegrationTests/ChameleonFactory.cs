using Microsoft.AspNetCore.Builder;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;

namespace Chameleon.IntegrationTests;

/// Boots the real Host in memory (CHAMELEON_STORE unset -> in-memory stores) and swaps the webhook
/// transport for a capturing handler, so tests assert on what the app under test would receive.
/// Delivery is asynchronous (outbox + worker): wait with SettledAsync before asserting on it.
public sealed class ChameleonFactory(Action<IServiceCollection>? extra = null) : WebApplicationFactory<Program>
{
    public WebhookCapture Webhooks { get; } = new();

    // Test-only key for issued connector secrets.
    public const string SecretKey = "Y2hhbWVsZW9uLXRlc3Qta2V5LTMyLWJ5dGVzLWxvbmc="; // gitleaks:allow

    // Test-only CA, one per test run.
    public static readonly (string CertPem, string KeyPem) Ca = CertificateAuthority.CreateRoot("Chameleon Test CA");

    /// The test server has no TLS: this header stands in for the client certificate of the handshake.
    public const string ClientCertificateHeader = "X-Test-Client-Certificate";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.UseSetting(SecretBox.KeyVariable, SecretKey)
            // Connectors come in as plugins, from the run's local feed and a catalog built from it.
            .UseSetting("CHAMELEON_CONNECTORS", Plugins.TestPlugins.Connectors)
            .UseSetting("CHAMELEON_NUGET_SOURCES", Plugins.TestPlugins.Feed)
            .UseSetting("CHAMELEON_PLUGINS_DIR", Plugins.TestPlugins.PluginsDir)
            .UseSetting("CHAMELEON_OFFLINE", "false")
            .UseSetting("CHAMELEON_CATALOG_URL", Plugins.TestPlugins.CatalogUrl)
            .UseSetting(CertificateAuthority.CertVariable, Ca.CertPem)
            .UseSetting(CertificateAuthority.KeyVariable, Ca.KeyPem)
            .ConfigureServices(services =>
        {
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientCertificate>();
            services.AddHttpClient<IWebhookDispatcher, HttpWebhookDispatcher>()
                .ConfigurePrimaryHttpMessageHandler(() => Webhooks);
            // mTLS webhooks go through the same capture, which records the certificate presented.
            services.AddSingleton<CertificateHandlerFactory>(_ => certificate =>
            {
                Webhooks.Certificates.Enqueue(certificate.Subject);
                return Webhooks;
            });
            // The MCP tools call this Host's control API: in memory, through the test server.
            services.AddHttpClient<Chameleon.Host.Mcp.ControlApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
            extra?.Invoke(services);
        });

    /// Waits until every webhook delivery of the sandbox reached a final state.
    public Task SettledAsync(string sandbox = Sandbox.Default, int timeoutMs = 10000) =>
        Eventually(() => Services.GetRequiredService<IWebhookOutbox>().Find(sandbox)
            .All(d => d.Status is DeliveryStatus.Delivered or DeliveryStatus.DeadLettered), timeoutMs);

    public static Task Eventually(Func<bool> check, int timeoutMs = 10000) => EventuallyAsync(() => Task.FromResult(check()), timeoutMs);

    public static async Task EventuallyAsync(Func<Task<bool>> check, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met before the deadline");
            await Task.Delay(50);
        }
    }
}

public sealed class WebhookCapture : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, string Body, long? ContentLength)> Received { get; } = new();
    public ConcurrentQueue<DateTimeOffset> ReceivedAt { get; } = new();
    /// Request headers of each call, in the order of Received.
    public ConcurrentQueue<Dictionary<string, string>> ReceivedHeaders { get; } = new();
    /// Subjects of the client certificates the mTLS webhook clients were built with.
    public ConcurrentQueue<string> Certificates { get; } = new();

    /// What the app under test answers to the n-th call (1-based); 200 by default.
    public Func<int, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Received.Enqueue((request.RequestUri!.ToString(), body, request.Content?.Headers.ContentLength));
        ReceivedAt.Enqueue(DateTimeOffset.UtcNow);
        ReceivedHeaders.Enqueue(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        var status = Respond(Received.Count);
        return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? "ok" : "error") };
    }
}

/// Test only: puts the certificate from the test header on the connection, as Kestrel would after the handshake.
internal sealed class TestClientCertificate : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use(async (http, inner) =>
        {
            if (http.Request.Headers[ChameleonFactory.ClientCertificateHeader].ToString() is { Length: > 0 } raw)
                http.Connection.ClientCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(Convert.FromBase64String(raw));
            await inner(http);
        });
        next(app);
    };
}

public static class FactoryExtensions
{
    /// Creates persistent, realtime sandboxes, as tests that need named long-lived environments expect.
    public static T WithPersistent<T>(this T factory, params string[] ids) where T : WebApplicationFactory<Program>
    {
        var store = factory.Services.GetRequiredService<ISandboxStore>();
        foreach (var id in ids)
            if (store.Get(id) is null)
                store.Put(new Sandbox { Id = id, Persistent = true, ClockMode = ClockMode.Realtime });
        return factory;
    }
}
