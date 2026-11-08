using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Chameleon.Engine;

namespace Chameleon.Sdk;

/// OAuth2 client_credentials as providers expose it, simulated: the token is a random value kept
/// per sandbox and per connector by its SHA-256 hash (never in clear) and validated on use.
/// Scopes granted are kept with the token and checked per route.
public static class SimulatedOAuth
{
    public const int DefaultTtlSeconds = 3600;

    public const string ClientIdEnv = "CLIENT_ID";
    public const string ClientSecretEnv = "CLIENT_SECRET";

    /// The credentials a connector using this OAuth issues on subscription.
    public static IReadOnlyList<IssuedCredential> Credentials(string tokenPath) =>
    [
        new("TOKEN_URL", "OAuth token URL", CredentialKind.Url, tokenPath),
        new(ClientIdEnv, "client_id", CredentialKind.Id),
        new(ClientSecretEnv, "client_secret", CredentialKind.Secret),
    ];

    public enum Access { Granted, Unauthenticated, MissingScope }

    /// Validates the grant and the client (the subscription's issued credentials) and
    /// returns the standard token response; scope = the scopes granted, space separated.
    public static IResult Issue(ConnectorContext ctx, string? grantType, string? clientId, string? clientSecret, string scope,
        int ttlSeconds = DefaultTtlSeconds)
    {
        if (grantType != "client_credentials" || string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request" });
        if (!ctx.CredentialMatches(ClientIdEnv, clientId) || (ctx.Subscription is not null && !ctx.CredentialMatches(ClientSecretEnv, clientSecret)))
            return Results.Json(new { error = "invalid_client" }, statusCode: 401);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var key = Key(ctx.Connector, token);
        ctx.Save(key, new OAuthToken { Key = key, ExpiresAt = ctx.Now.AddSeconds(ttlSeconds), Scopes = scope });

        return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = ttlSeconds, scope });
    }

    /// The client sent as HTTP Basic (RFC 6749 2.3.1); nulls when absent or malformed, so Issue refuses it.
    public static (string? Id, string? Secret) BasicClient(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return (null, null);
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? (null, null) : (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    /// Null when the bearer may call a route needing this scope; else 401, or 403 in the RFC 7807 shape.
    public static IResult? Refuse(ConnectorContext ctx, HttpContext http, string scope) => Check(ctx, http, scope) switch
    {
        Access.Granted => null,
        Access.MissingScope => Results.Json(new { title = "Acesso negado.", status = 403, detail = $"escopo necessario: {scope}" },
            statusCode: 403, contentType: "application/problem+json"),
        _ => Results.Unauthorized(),
    };

    /// The bearer must be valid and, when a scope is given, have been granted it.
    public static Access Check(ConnectorContext ctx, HttpContext http, string? scope)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Access.Unauthenticated;
        var token = header["Bearer ".Length..].Trim();
        if (ctx.Store.Get<OAuthToken>(Key(ctx.Connector, token)) is not { } issued || ctx.Now >= issued.ExpiresAt) return Access.Unauthenticated;
        return scope is null || issued.Scopes is null || issued.Scopes.Split(' ').Contains(scope) ? Access.Granted : Access.MissingScope;
    }

    private static string Key(string connector, string token) =>
        $"{connector}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))}";
}

public sealed class OAuthToken : IHasNaturalKey
{
    public required string Key { get; init; }
    public string NaturalKeyProperty => nameof(Key);
    public required DateTimeOffset ExpiresAt { get; init; }
    /// Granted scopes, space separated; null on tokens issued before scopes were kept (all granted).
    public string? Scopes { get; init; }
}
