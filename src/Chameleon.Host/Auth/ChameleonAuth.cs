using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Chameleon.Engine;
using Chameleon.Host.Http;
using Chameleon.Host.Live;

namespace Chameleon.Host.Auth;

/// Authentication with CHAMELEON_AUTH=oidc. Three credentials, all as Authorization: Bearer: a JWT from the
/// configured OIDC provider (OPERATOR, or AGENT when issued to another client), a personal token chm_pat_ (AGENT)
/// and the CI key chm_ci_ (CI). With none, no login: OPERATOR.
public static class ChameleonAuth
{
    public const string CiKeyPrefix = "chm_ci_";
    public const string Operator = "OPERATOR", Agent = "AGENT", Ci = "CI";

    private const string Scheme = "chameleon";
    private const string TokenScheme = "chameleon-token";
    private const string EmailClaim = "email";
    /// Private claim type: only Chameleon sets the role, nothing in the provider token decides it.
    public const string RoleClaim = "chameleon_role";
    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";

    /// Control routes anyone may call: the panel boots from them before login.
    private static readonly string[] PublicPaths = ["/config", "/ca.crt"];

    public static bool IsEnabled(IConfiguration configuration) => AuthSettings.From(configuration).Mode == AuthMode.Oidc;

    /// The instance never starts half configured.
    public static void Validate(IConfiguration configuration)
    {
        var settings = AuthSettings.From(configuration);
        if (settings.Mode == AuthMode.None) return;
        var missing = new List<string>();
        if (settings.Issuer is null) missing.Add(AuthSettings.IssuerKey);
        if (settings.ClientId is null) missing.Add(AuthSettings.ClientIdKey);
        if (settings.AllowedDomains.Length == 0 && settings.AllowedEmails.Length == 0)
            missing.Add($"{AuthSettings.AllowedDomainsKey} or {AuthSettings.AllowedEmailsKey}");
        foreach (var key in new[] { WebhookDestinationPolicy.AllowedHostsKey, SecretBox.KeyVariable, CertificateAuthority.CertVariable, CertificateAuthority.KeyVariable })
            if (string.IsNullOrWhiteSpace(configuration[key])) missing.Add(key);
        if (missing.Count > 0)
            throw new InvalidOperationException($"{AuthSettings.ModeKey}=oidc requires {string.Join(", ", missing)}");
        if (settings.CiKey is { } ci && !ci.StartsWith(CiKeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{AuthSettings.CiKeyKey} must start with {CiKeyPrefix}");
        if (!Uri.TryCreate(settings.Issuer, UriKind.Absolute, out var issuer) || (issuer.Scheme != Uri.UriSchemeHttps && !IsLocalIssuer(issuer)))
            throw new InvalidOperationException($"{AuthSettings.IssuerKey} must be an https URL (http only for a local issuer)");
    }

    /// D10: a local issuer (tests, compose) may serve its discovery over http.
    public static bool IsLocalIssuer(Uri issuer) =>
        issuer.IsLoopback || string.Equals(issuer.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);

    public static void AddChameleonAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = AuthSettings.From(configuration);
        if (settings.Mode != AuthMode.Oidc) return;

        services.AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, null, o => o.ForwardDefaultSelector = http =>
                Bearer(http)?.StartsWith("chm_", StringComparison.Ordinal) == true ? TokenScheme : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TokenHandler>(TokenScheme, null)
            .AddJwtBearer(o =>
            {
                o.Authority = settings.Issuer;
                o.MetadataAddress = $"{settings.Issuer}/.well-known/openid-configuration";
                o.RequireHttpsMetadata = !IsLocalIssuer(new Uri(settings.Issuer!));
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    // Auth0 issues iss with a trailing slash; the setting is stored without it.
                    ValidIssuers = [settings.Issuer, settings.Issuer + "/"],
                    ValidAudience = settings.ExpectedAudience,
                    ValidateIssuerSigningKey = true,
                };
                o.Events = new JwtBearerEvents
                {
                    // D8: WebSockets cannot send headers.
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Headers.Authorization.Count == 0 && HubToken(ctx.HttpContext) is { } token) ctx.Token = token;
                        return Task.CompletedTask;
                    },
                    // Valid token but not an allowed, verified e-mail: authenticated with no role, so 403.
                    OnTokenValidated = ctx =>
                    {
                        var principal = ctx.Principal!;
                        var identity = (ClaimsIdentity)principal.Identity!;
                        foreach (var forged in identity.FindAll(RoleClaim).ToList()) identity.RemoveClaim(forged);
                        if (principal.FindFirst(EmailClaim)?.Value is { } email && IsVerified(principal) && settings.IsAllowed(email))
                            identity.AddClaim(new Claim(RoleClaim, RoleOfClient(principal, settings)));
                        return Task.CompletedTask;
                    },
                };
            });
    }

    private static bool IsVerified(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    /// D3: a token the provider issued to another client is that client acting for the operator.
    private static string RoleOfClient(ClaimsPrincipal principal, AuthSettings settings) =>
        (principal.FindFirst("azp") ?? principal.FindFirst("client_id"))?.Value is { } client && client != settings.ClientId ? Agent : Operator;

    /// Only on the live channel: elsewhere a token in the URL ends up in logs and history.
    private static string? HubToken(HttpContext http) =>
        http.Request.Path.StartsWithSegments(LiveHub.Path) && http.Request.Query["access_token"] is { Count: 1 } q && q[0] is { Length: > 0 } t ? t : null;

    /// After the sandbox prefix is stripped: every control route but the public ones needs a role.
    public static void UseChameleonAuth(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration)) return;
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path;
            if (!path.StartsWithSegments(ChameleonEndpoints.ControlPrefix, out var rest) || IsPublic(rest, http.Request.Method))
            {
                await next(http);
                return;
            }
            if (http.User.Identity?.IsAuthenticated != true)
            {
                // RFC 9728: tells an MCP client where to find the authorization server.
                http.Response.Headers.WWWAuthenticate =
                    $"Bearer resource_metadata=\"{PublicOrigin(http)}{ResourceMetadataPath}{http.Request.PathBase}{http.Request.Path}\"";
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await http.Response.WriteAsJsonAsync(new { message = "login required" });
                return;
            }
            if (RoleOf(http) is null)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(new { message = "access restricted to allowed, verified e-mails" });
                return;
            }
            await next(http);
        });
    }

    private static bool IsPublic(PathString rest, string method) =>
        PublicPaths.Any(p => rest.Equals(p, StringComparison.OrdinalIgnoreCase))
        || ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            && rest.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["connectors", _, var last]
            && last.Equals("logo", StringComparison.OrdinalIgnoreCase));

    /// The caller's role; locally always OPERATOR. Null: authenticated without a role.
    public static string? RoleOf(HttpContext http) =>
        IsEnabled(http.RequestServices.GetRequiredService<IConfiguration>())
            ? http.User.FindFirst(RoleClaim)?.Value
            : Operator;

    public const string PublicUrlKey = "CHAMELEON_PUBLIC_URL";

    /// The address clients use: the configured public URL (behind a proxy), else the request's.
    public static string PublicOrigin(HttpContext http) =>
        http.RequestServices.GetRequiredService<IConfiguration>()[PublicUrlKey] is { Length: > 0 } url
            ? url.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}";

    public static string? EmailOf(HttpContext http) => http.User.FindFirst(EmailClaim)?.Value;

    /// 403 unless the caller has one of the roles.
    public static IResult? Require(HttpContext http, params string[] roles) =>
        roles.Contains(RoleOf(http)) ? null : Results.Json(new { message = "action not allowed for this access" }, statusCode: StatusCodes.Status403Forbidden);

    private static string? Bearer(HttpContext http) =>
        http.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : HubToken(http);

    /// chm_ci_ (compared in constant time) and chm_pat_ (by hash, not revoked).
    private sealed class TokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IConfiguration configuration, IAccessTokenStore tokens)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Bearer(Context) is not { } token) return Task.FromResult(AuthenticateResult.NoResult());

            if (token.StartsWith(CiKeyPrefix, StringComparison.Ordinal))
                return Task.FromResult(CryptographicOperations.FixedTimeEquals(Sha(token), Sha(configuration[AuthSettings.CiKeyKey] ?? ""))
                    ? Success(Ci, email: null)
                    : AuthenticateResult.Fail("invalid CI key"));

            if (token.StartsWith(AccessToken.Prefix, StringComparison.Ordinal)
                && tokens.FindByHash(AccessToken.Hash(token)) is { RevokedAt: null } found)
            {
                var utcNow = DateTimeOffset.UtcNow;
                if (found.LastUsedAt is null || utcNow - found.LastUsedAt > TouchEvery) tokens.Touch(found.Id, utcNow);
                return Task.FromResult(Success(Agent, found.OwnerEmail));
            }
            return Task.FromResult(AuthenticateResult.Fail("invalid or revoked token"));
        }

        private AuthenticateResult Success(string role, string? email)
        {
            var claims = new List<Claim> { new(RoleClaim, role) };
            if (email is not null) claims.Add(new Claim(EmailClaim, email));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, TokenScheme)), TokenScheme));
        }

        private static byte[] Sha(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }
}
