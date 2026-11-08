using Chameleon.Engine;
using Chameleon.Host.Http;

namespace Chameleon.Host.Auth;

/// What the panel needs to log in (/config), who is calling (/me) and personal tokens for the MCP (/tokens).
public static class AuthEndpoints
{
    public const int MaxTokenName = 60;
    private const string Prefix = ChameleonEndpoints.ControlPrefix;
    /// Local owner of personal tokens: there is no login, so no e-mail.
    private const string LocalOwner = "local";

    public sealed record CreateTokenRequest(string? Name);

    public static void MapAuth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet($"{Prefix}/config", (IConfiguration config) =>
        {
            var settings = AuthSettings.From(config);
            var oidc = settings.Mode == AuthMode.Oidc;
            return Results.Ok(new
            {
                auth = oidc ? "oidc" : "none",
                oidc = oidc ? new { issuer = settings.Issuer, clientId = settings.ClientId, audience = settings.Audience, scope = "openid email profile" } : null,
                // A hint for the login screen; the exact e-mails of the allowlist stay private.
                allowedEmailDomains = oidc ? settings.AllowedDomains : [],
            });
        });

        // RFC 9728 protected resource metadata, per resource path (/_chameleon/mcp, /s/{sandbox}/_chameleon/mcp):
        // the MCP client discovers the OIDC provider here; dynamic client registration depends on the provider.
        routes.MapGet($"{ChameleonAuth.ResourceMetadataPath}/{{**resource}}", (string resource, HttpContext http, IConfiguration config) =>
            ChameleonAuth.IsEnabled(config)
                ? Results.Ok(new
                {
                    resource = $"{ChameleonAuth.PublicOrigin(http)}/{resource}",
                    authorization_servers = new[] { AuthSettings.From(config).Issuer! },
                    bearer_methods_supported = new[] { "header" },
                    resource_name = "Chameleon",
                })
                : Results.NotFound());

        routes.MapGet($"{Prefix}/me", (HttpContext http) =>
            Results.Ok(new { email = ChameleonAuth.EmailOf(http), role = ChameleonAuth.RoleOf(http) }));

        routes.MapPost($"{Prefix}/tokens", (CreateTokenRequest req, HttpContext http, IAccessTokenStore tokens) =>
        {
            if (ChameleonAuth.Require(http, ChameleonAuth.Operator) is { } denied) return denied;
            if (req.Name?.Trim() is not { Length: > 0 and <= MaxTokenName } name)
                return Results.BadRequest(new { message = $"token name: 1 to {MaxTokenName} characters" });

            var (value, hash) = AccessToken.Generate();
            var token = new AccessToken { OwnerEmail = Owner(http), Name = name, TokenHash = hash };
            tokens.Add(token);
            return Results.Created($"{Prefix}/tokens/{token.Id}", new { token.Id, token.Name, token = value, token.CreatedAt });
        });

        routes.MapGet($"{Prefix}/tokens", (HttpContext http, IAccessTokenStore tokens) =>
            ChameleonAuth.Require(http, ChameleonAuth.Operator)
            ?? Results.Ok(tokens.List(Owner(http)).Select(t => new { t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.RevokedAt })));

        routes.MapDelete($"{Prefix}/tokens/{{id:guid}}", (Guid id, HttpContext http, IAccessTokenStore tokens) =>
            ChameleonAuth.Require(http, ChameleonAuth.Operator)
            ?? (tokens.Revoke(Owner(http), id, DateTimeOffset.UtcNow)
                ? Results.NoContent()
                : Results.NotFound(new { message = "token not found" })));
    }

    private static string Owner(HttpContext http) => ChameleonAuth.EmailOf(http) ?? LocalOwner;
}
