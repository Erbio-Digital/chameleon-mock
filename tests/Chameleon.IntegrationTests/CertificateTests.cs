using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using Chameleon.Host.Http;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Client certificate on the provider surface and on webhooks.
public sealed class CertificateTests
{
    private static readonly CertificateAuthority Ca = new(ChameleonFactory.Ca.CertPem, ChameleonFactory.Ca.KeyPem);

    [Fact]
    public void Ca_issues_signs_csr_and_tells_its_own_certificates_apart()
    {
        var (pem, key) = Ca.IssueClient("teste.client.chameleon");
        using var issued = X509Certificate2.CreateFromPem(pem, key);
        Assert.True(Ca.Issued(issued));

        var other = CertificateAuthority.CreateRoot("Outra CA");
        var (otherPem, _) = new CertificateAuthority(other.CertPem, other.KeyPem).IssueClient("x");
        using var foreign = X509Certificate2.CreateFromPem(otherPem);
        Assert.False(Ca.Issued(foreign));

        using var appKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=app", appKey, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        using var signed = X509Certificate2.CreateFromPem(Ca.SignRequest(csr, "bradesco.client.chameleon"));
        Assert.True(Ca.Issued(signed));
        Assert.Equal(appKey.ExportSubjectPublicKeyInfo(), signed.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.Throws<InvalidCertificateRequestException>(() => Ca.SignRequest("not a csr", "x"));
        Assert.Throws<InvalidOperationException>(() => new CertificateAuthority(null, null));
    }

    [Fact]
    public async Task Service_with_the_sandbox_certificate_is_served_and_nothing_of_it_is_recorded()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Bradesco(qa, "qa"); // subscribes and presents the issued certificate
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(qa, NewTxId())).StatusCode);

        var detail = await Subscription(qa);
        var cert = detail.GetProperty("certificate");
        Assert.Equal("Generated", cert.GetProperty("source").GetString());
        Assert.Contains("PRIVATE KEY", cert.GetProperty("keyPem").GetString());
        Assert.Equal(Ca.CertificatePem, cert.GetProperty("caPem").GetString());
        Assert.Equal(Ca.CertificatePem, await qa.GetStringAsync("_chameleon/ca.crt"));

        var journal = await qa.GetStringAsync("_chameleon/journal?connector=bradesco");
        Assert.DoesNotContain("CERTIFICATE", journal);
        Assert.DoesNotContain("PRIVATE KEY", journal);
    }

    [Fact]
    public async Task Pfx_asks_for_a_password_once_keeps_it_sealed_and_survives_rotation()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Bradesco(qa, "qa");
        const string pfx = "_chameleon/connectors/bradesco/subscription/certificate.pfx";
        const string password = "_chameleon/connectors/bradesco/subscription/certificate/pfx-password";

        Assert.Equal(HttpStatusCode.Conflict, (await qa.GetAsync(pfx)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await qa.PutAsJsonAsync(password, new { password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await qa.PutAsJsonAsync(password, new { password = "pfx-password-1" })).StatusCode);

        using (var loaded = new X509Certificate2(await qa.GetByteArrayAsync(pfx), "pfx-password-1"))
        {
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(await CertificatePem(qa), loaded.ExportCertificatePem());
        }
        Assert.Equal("pfx-password-1", (await Subscription(qa)).GetProperty("certificate").GetProperty("pfxPassword").GetString());

        Assert.Equal(HttpStatusCode.OK, (await qa.PostAsync("_chameleon/connectors/bradesco/subscription/rotate", null)).StatusCode);
        using (var rotated = new X509Certificate2(await qa.GetByteArrayAsync(pfx), "pfx-password-1"))
            Assert.Equal(await CertificatePem(qa), rotated.ExportCertificatePem());

        var journal = await qa.GetStringAsync("_chameleon/journal?connector=bradesco");
        Assert.DoesNotContain("pfx-password-1", journal);
    }

    [Fact]
    public async Task Call_without_certificate_is_dropped_outside_default()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Subscribe(qa, "bradesco");
        qa.DefaultRequestHeaders.Remove(ChameleonFactory.ClientCertificateHeader);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            TestAuth.BradescoToken(qa, "x"));
        var journal = await qa.GetFromJsonAsync<JsonElement>("_chameleon/journal?connector=bradesco&kind=InboundRequest");
        Assert.Empty(journal.EnumerateArray());

        var local = await TestAuth.BradescoToken(factory.CreateClient(), "local");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Certificate_of_another_sandbox_rotated_or_cancelled_is_refused()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var qa = Client(factory, "qa");
        var hml = Client(factory, "hml");
        await TestAuth.Subscribe(qa, "bradesco");
        await TestAuth.Subscribe(hml, "bradesco");
        var qaCert = await CertificatePem(qa);

        TestAuth.Present(hml, qaCert);
        var refused = await TestAuth.BradescoToken(hml, "x");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("does not belong to this sandbox", await refused.Content.ReadAsStringAsync());
        var journal = await hml.GetFromJsonAsync<JsonElement>("_chameleon/journal?connector=bradesco&kind=InboundRequest");
        Assert.Equal(403, journal.EnumerateArray().Single().GetProperty("statusCode").GetInt32());

        (await qa.PostAsync("_chameleon/connectors/bradesco/subscription/rotate", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await Ping(qa)).StatusCode);
        TestAuth.Present(qa, await CertificatePem(qa));
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Ping(qa)).StatusCode);

        (await qa.DeleteAsync("_chameleon/connectors/bradesco/subscription")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Ping(qa)).StatusCode);
    }

    [Fact]
    public async Task App_csr_is_signed_and_replaces_the_generated_certificate()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Subscribe(qa, "bradesco");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await qa.PostAsJsonAsync("_chameleon/connectors/bradesco/subscription/certificate", new { csr = "lixo" })).StatusCode);

        using var appKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=minha-app", appKey, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var signed = await qa.PostAsJsonAsync("_chameleon/connectors/bradesco/subscription/certificate", new { csr });
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal("Csr", (await signed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await Ping(qa)).StatusCode); // the generated one no longer counts
        var cert = (await Subscription(qa)).GetProperty("certificate");
        Assert.Equal(JsonValueKind.Null, cert.GetProperty("keyPem").ValueKind);
        TestAuth.Present(qa, cert.GetProperty("pem").GetString()!);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Ping(qa)).StatusCode);

        var journal = await qa.GetStringAsync("_chameleon/journal?connector=bradesco&kind=ControlAction");
        Assert.DoesNotContain("BEGIN CERTIFICATE REQUEST", journal);
        Assert.Equal(HttpStatusCode.NotFound,
            (await qa.PostAsJsonAsync("_chameleon/connectors/nfse-sp/subscription/certificate", new { csr })).StatusCode);
    }

    [Fact]
    public async Task Rotating_in_a_copy_does_not_change_the_source_certificate()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Subscribe(qa, "bradesco");
        var before = await CertificatePem(qa);
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-3", from = "qa" })).EnsureSuccessStatusCode();
        var copy = Client(factory, "ci-3");
        TestAuth.Present(copy, before);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Ping(copy)).StatusCode);

        (await copy.PostAsync("_chameleon/connectors/bradesco/subscription/rotate", null)).EnsureSuccessStatusCode();
        Assert.Equal(before, await CertificatePem(qa));
        Assert.NotEqual(before, await CertificatePem(copy));
    }

    [Fact]
    public async Task Bradesco_webhook_over_https_carries_its_certificate()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = Client(factory, "qa");
        await TestAuth.Bradesco(qa, "qa");
        var txid = NewTxId();
        await CreateCob(qa, txid);
        (await qa.PostAsync($"_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null)).EnsureSuccessStatusCode();
        await factory.SettledAsync("qa");

        Assert.Equal("CN=bradesco.webhook.chameleon", Assert.Single(factory.Webhooks.Certificates));
        var attempt = await Verify(qa, new { connector = "bradesco", entityId = txid, kind = "OutboundWebhook" });
        Assert.Contains("with the provider certificate", attempt.GetProperty("matches")[0].GetProperty("detail").GetString());
    }

    [Theory] // with the mTLS port on, the plain HTTP URLs are listened to by hand: the image's http://+:8080 must stay on all interfaces
    [InlineData("http://+:8080", true)]
    [InlineData("http://*:8080", true)]
    [InlineData("http://0.0.0.0:8080", true)]
    [InlineData("http://[::]:8080", true)]
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://127.0.0.1:5000", false)]
    public void Mtls_listener_keeps_wildcard_urls_on_all_interfaces(string url, bool any) =>
        Assert.Equal(any, MtlsEndpoint.IsAnyHost(Microsoft.AspNetCore.Http.BindingAddress.Parse(url).Host));

    [Fact] // The mTLS port refuses a missing or foreign certificate before any code runs
    public async Task Mtls_port_requires_a_certificate_from_the_chameleon_ca()
    {
        await using var server = await Server(listen => MtlsEndpoint.Configure(listen, Ca, ["localhost"]));
        var (pem, key) = Ca.IssueClient("bradesco.client.chameleon");

        using (var ok = Client(server.Url, Usable(X509Certificate2.CreateFromPem(pem, key))))
            Assert.Equal("ok", await ok.GetStringAsync("/"));

        using (var none = Client(server.Url, null))
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => none.GetStringAsync("/"));

        var other = CertificateAuthority.CreateRoot("Outra CA");
        var (otherPem, otherKey) = new CertificateAuthority(other.CertPem, other.KeyPem).IssueClient("x");
        using (var foreign = Client(server.Url, Usable(X509Certificate2.CreateFromPem(otherPem, otherKey))))
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => foreign.GetStringAsync("/"));
    }

    [Fact] // A receiver that trusts the Chameleon CA accepts the webhook; one that trusts another refuses
    public async Task Webhook_certificate_is_accepted_only_by_a_receiver_trusting_the_chameleon_ca()
    {
        var policy = new WebhookDestinationPolicy(["localhost"]);
        var realProvider = CertificateAuthority.CreateRoot("Provedor real CA");

        await using var trusting = await Receiver(Ca);
        await using var strict = await Receiver(new CertificateAuthority(realProvider.CertPem, realProvider.KeyPem));
        var clients = new CertificateWebhookClients(certificate =>
        {
            var handler = (System.Net.Http.SocketsHttpHandler)CertificateWebhookClients.Default(policy, certificate);
            // Test receivers use a certificate from the test CA; the dispatcher keeps its normal server check otherwise.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Ca.Issued(new X509Certificate2(cert));
            return handler;
        });
        var dispatcher = new HttpWebhookDispatcher(new HttpClient(policy.CreateHandler()), policy, clients);
        var webhook = Ca.WebhookClient("bradesco");

        var delivered = await dispatcher.SendAsync(trusting.Url + "/pix", "{}", TimeSpan.FromSeconds(5), CancellationToken.None, webhook);
        Assert.Equal(200, delivered.StatusCode);
        Assert.Equal("CN=bradesco.webhook.chameleon", trusting.LastClient);

        var refused = await dispatcher.SendAsync(strict.Url + "/pix", "{}", TimeSpan.FromSeconds(5), CancellationToken.None, webhook);
        Assert.Null(refused.StatusCode);
        Assert.False(refused.Blocked);
        Assert.NotNull(refused.Error);
    }

    // ---- helpers ----

    private static HttpClient Client(ChameleonFactory factory, string sandbox)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        return http;
    }

    private static Task<HttpResponseMessage> Ping(HttpClient http) =>
        TestAuth.BradescoToken(http, "x");

    private static async Task<JsonElement> Subscription(HttpClient http) =>
        await http.GetFromJsonAsync<JsonElement>("_chameleon/connectors/bradesco/subscription");

    private static async Task<string> CertificatePem(HttpClient http) =>
        (await Subscription(http)).GetProperty("certificate").GetProperty("pem").GetString()!;

    private static X509Certificate2 Usable(X509Certificate2 withKey)
    {
        using (withKey) return new X509Certificate2(withKey.Export(X509ContentType.Pkcs12));
    }

    private static HttpClient Client(string baseUrl, X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && Ca.Issued(cert),
        };
        if (certificate is not null) handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }

    private sealed class RunningServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public string? LastClient { get; set; }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<RunningServer> Server(Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> https, Func<HttpContext, string>? respond = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, https));
        var app = builder.Build();
        RunningServer? running = null;
        app.Run(async http =>
        {
            running!.LastClient = http.Connection.ClientCertificate?.Subject;
            await http.Response.WriteAsync(respond?.Invoke(http) ?? "ok");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("127.0.0.1", "localhost");
        running = new RunningServer(app, address);
        return running;
    }

    /// An app webhook endpoint that requires a client certificate and trusts only the given CA.
    private static Task<RunningServer> Receiver(CertificateAuthority trusted)
    {
        var serverCertificate = Ca.IssueServer(["localhost"]);
        return Server(listen => listen.UseHttps(o =>
        {
            o.ServerCertificate = serverCertificate;
            o.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            o.ClientCertificateValidation = (cert, _, _) => trusted.Issued(cert);
        }));
    }
}
