using System.Security.Cryptography.X509Certificates;
using Chameleon.Sdk;
using Chameleon.Engine;
using Chameleon.Host.Auth;

namespace Chameleon.Host.Http;

/// Connector subscription per sandbox. Reads stay outside the connector group,
/// so a panel refresh is not journaled and issued secrets never reach the journal;
/// changes run inside it as control actions and answer without secrets.
public static class SubscriptionEndpoints
{
    public const string NotSubscribed = "connector not subscribed in this sandbox";
    public const string ForeignCertificate = "client certificate does not belong to this sandbox";
    /// Public base URL of the mTLS surface (the local https port, or the mTLS domain of a deployment).
    public const string MtlsUrlVariable = "CHAMELEON_MTLS_URL";

    public static IEndpointRouteBuilder MapSubscriptions(this IEndpointRouteBuilder routes)
    {
        var prefix = ChameleonEndpoints.ControlPrefix;

        routes.MapGet($"{prefix}/subscriptions", (IEnumerable<IConnector> connectors, RequestScope store) =>
            Results.Ok(connectors.Select(c => c.Describe()).Select(d => Find(store, d.Name) is { } s
                ? new { connector = d.Name, status = s.Status(d), subscribedAt = (DateTimeOffset?)s.SubscribedAt, missing = s.Missing(d) }
                : new { connector = d.Name, status = SubscriptionStatus.Available, subscribedAt = (DateTimeOffset?)null, missing = (IReadOnlyList<string>)[] })));

        routes.MapGet($"{prefix}/connectors/{{connector}}/subscription", (string connector, IEnumerable<IConnector> connectors,
            RequestScope store, SecretBox secrets, CertificateAuthority ca, IConfiguration config, SandboxContext sandbox, HttpContext http) =>
        {
            if (connectors.FirstOrDefault(c => c.Name == connector)?.Describe() is not { } d)
                return Results.NotFound(new { message = $"conector desconhecido: {connector}" });
            if (Find(store, connector) is not { } s)
                return Results.Ok(new { status = SubscriptionStatus.Available, credentials = Array.Empty<object>() });

            var origin = d.ClientCertificate && config[MtlsUrlVariable] is { Length: > 0 } mtls
                ? mtls.TrimEnd('/')
                : $"{http.Request.Scheme}://{http.Request.Host}";
            var baseUrl = $"{origin}/s/{sandbox.Id}/{connector}";
            var credentials = d.Credentials.Select(c => new
            {
                env = c.Env,
                label = c.Label,
                secret = c.Kind == CredentialKind.Secret,
                value = c.Kind == CredentialKind.Url ? baseUrl + c.Path : s.Value(d, secrets, c.Env),
            }).Prepend(new { env = "BASE_URL", label = "URL base", secret = false, value = (string?)baseUrl });
            // An agent never gets the private key or the .pfx password, not even through the API.
            var privateParts = ChameleonAuth.RoleOf(http) != ChameleonAuth.Agent;
            var certificate = s.Certificate is { } c
                ? new
                {
                    pem = c.Pem,
                    keyPem = c.KeySealed is null || !privateParts ? null : secrets.Decrypt(c.KeySealed),
                    pfxPassword = s.PfxPasswordSealed is null || !privateParts ? null : secrets.Decrypt(s.PfxPasswordSealed),
                    caPem = ca.CertificatePem,
                    c.Thumbprint,
                    c.NotAfter,
                    source = c.Source.ToString(),
                }
                : null;
            return Results.Ok(new { status = s.Status(d), s.SubscribedAt, s.RotatedAt, credentials, s.Settings, missing = s.Missing(d), certificate });
        });

        // The client certificate as PKCS#12 (.pfx), the format .NET loads with X509Certificate2. Built on
        // demand from the sealed key and the password the operator chose; a read, so never journaled.
        routes.MapGet($"{prefix}/connectors/{{connector}}/subscription/certificate.pfx", (string connector, RequestScope store, SecretBox secrets, HttpContext http) =>
        {
            if (ChameleonAuth.Require(http, ChameleonAuth.Operator, ChameleonAuth.Ci) is { } denied) return denied;
            if (Find(store, connector) is not { } s) return Results.NotFound(new { message = NotSubscribed });
            if (s.Certificate is not { KeySealed: { } keySealed } c)
                return Results.Conflict(new { message = "certificate signed from a CSR: the private key stays with the app" });
            if (s.PfxPasswordSealed is null) return Results.Conflict(new { message = PfxPasswordMissing });
            using var cert = X509Certificate2.CreateFromPem(c.Pem, secrets.Decrypt(keySealed));
            return Results.File(cert.Export(X509ContentType.Pkcs12, secrets.Decrypt(s.PfxPasswordSealed)), "application/x-pkcs12", $"{connector}.pfx");
        });
        return routes;
    }

    /// Changes, mapped inside the connector's group (journaled as control actions).
    public static void MapSubscriptionChanges(this RouteGroupBuilder group, IConnector connector)
    {
        var path = $"{ChameleonEndpoints.ControlPrefix}/connectors/{connector.Name}/subscription";

        group.MapPost(path, (HttpContext http, ConnectorContext ctx, SecretBox secrets, CertificateAuthority ca) =>
        {
            http.Annotate(entityId: connector.Name);
            if (ctx.Subscription is not null) return Results.Conflict(new { message = "connector already subscribed in this sandbox" });
            var d = connector.Describe();
            var s = ConnectorSubscription.Issue(d, secrets, ca, ctx.Now);
            ctx.Save(connector.Name, s);
            return Results.Json(new { status = s.Status(d) }, statusCode: 201);
        });

        group.MapDelete(path, (HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: connector.Name);
            if (ctx.Subscription is not { } s) return Results.NotFound(new { message = NotSubscribed });
            ctx.Save(connector.Name, new ConnectorSubscription { Connector = s.Connector, Active = false, SubscribedAt = s.SubscribedAt });
            return Results.NoContent();
        });

        group.MapPost($"{path}/rotate", (HttpContext http, ConnectorContext ctx, SecretBox secrets, CertificateAuthority ca) =>
        {
            http.Annotate(entityId: connector.Name);
            if (ctx.Subscription is not { } s) return Results.NotFound(new { message = NotSubscribed });
            var d = connector.Describe();
            var rotated = ConnectorSubscription.Issue(d, secrets, ca, ctx.Now, s.Settings, s.SubscribedAt, s.PfxPasswordSealed);
            ctx.Save(connector.Name, rotated);
            return Results.Ok(new { status = rotated.Status(d), rotated.RotatedAt });
        });

        group.MapPut($"{path}/settings", (Dictionary<string, string?> body, HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: connector.Name);
            if (ctx.Subscription is not { } s) return Results.NotFound(new { message = NotSubscribed });
            var d = connector.Describe();
            var settings = new Dictionary<string, string>(s.Settings);
            foreach (var (key, raw) in body)
            {
                if (d.Settings.FirstOrDefault(x => x.Key == key) is not { } setting)
                    return Results.BadRequest(new { message = $"dado desconhecido: {key}", known = d.Settings.Select(x => x.Key) });
                var value = raw?.Trim();
                if (string.IsNullOrEmpty(value)) settings.Remove(key);
                else if (setting.Kind == "url" && !WebhookRegistrations.IsValidUrl(value))
                    return Results.BadRequest(new { message = $"{setting.Label}: use an absolute http or https URL" });
                else settings[key] = value;
            }
            var updated = s with { Settings = settings };
            ctx.Save(connector.Name, updated);
            return Results.Ok(new { status = updated.Status(d), missing = updated.Missing(d) });
        });

        // The app's own CSR, signed by the Chameleon CA; the generated key is dropped.
        if (connector.Describe().ClientCertificate)
            group.MapPost($"{path}/certificate", (CertificateSigningRequest req, HttpContext http, ConnectorContext ctx, CertificateAuthority ca) =>
            {
                http.Annotate(entityId: connector.Name);
                if (ctx.Subscription is not { } s) return Results.NotFound(new { message = NotSubscribed });
                string pem;
                try
                {
                    pem = ca.SignRequest(req.Csr ?? "", ConnectorSubscription.CommonName(connector.Describe()));
                }
                catch (InvalidCertificateRequestException invalid)
                {
                    return Results.BadRequest(new { message = invalid.Message });
                }
                var signed = IssuedCertificate.From(pem, null, CertificateSource.Csr);
                ctx.Save(connector.Name, s with { RotatedAt = ctx.Now, Certificate = signed });
                return Results.Ok(new { signed.Thumbprint, signed.NotAfter, source = signed.Source.ToString() });
            });

        // Password of the .pfx export, asked on the first download. The journal redacts "password".
        if (connector.Describe().ClientCertificate)
            group.MapPut($"{path}/certificate/pfx-password", (PfxPasswordRequest req, HttpContext http, ConnectorContext ctx, SecretBox secrets) =>
            {
                http.Annotate(entityId: connector.Name);
                if (ChameleonAuth.Require(http, ChameleonAuth.Operator, ChameleonAuth.Ci) is { } denied) return denied;
                if (ctx.Subscription is not { } s) return Results.NotFound(new { message = NotSubscribed });
                if (req.Password is not { Length: >= MinPfxPassword } password)
                    return Results.BadRequest(new { message = $".pfx password: use at least {MinPfxPassword} characters" });
                ctx.Save(connector.Name, s with { PfxPasswordSealed = secrets.Encrypt(password) });
                return Results.NoContent();
            });

        group.MapPost($"{path}/test-event", async (HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: connector.Name);
            if (ctx.Subscription is null) return Results.NotFound(new { message = NotSubscribed });
            var events = connector.Describe().TestEvents?.Invoke(ctx) ?? [];
            if (events.Count == 0)
                return Results.Conflict(new { message = "no destination configured: register the webhook or fill in the service settings" });
            foreach (var e in events)
                await ctx.NotifyAsync(TestEntityId, e.Url, e.Payload, "test event");
            return Results.Json(new { sent = events.Count }, statusCode: 202);
        });
    }

    public const string TestEntityId = "test-event";

    public sealed record CertificateSigningRequest(string? Csr);

    public sealed record PfxPasswordRequest(string? Password);

    public const int MinPfxPassword = 8;
    public const string PfxPasswordMissing = "set the .pfx password before downloading";

    private static ConnectorSubscription? Find(RequestScope store, string connector) =>
        store.Get<ConnectorSubscription>(connector) is { Active: true } s ? s : null;
}
