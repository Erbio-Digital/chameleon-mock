using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chameleon.IntegrationTests.Auth;

/// An OIDC provider in memory: discovery document, JWKS and tokens signed with its key.
public sealed class FakeOidcIssuer : HttpMessageHandler
{
    public const string Issuer = "https://idp.example.com/realms/test";
    public const string ClientId = "chameleon-panel";
    private readonly ECDsaSecurityKey _key = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test" };
    public int DiscoveryRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsoluteUri;
        string? body = null;
        if (path == $"{Issuer}/.well-known/openid-configuration")
        {
            DiscoveryRequests++;
            body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["issuer"] = Issuer, ["jwks_uri"] = $"{Issuer}/jwks",
                ["authorization_endpoint"] = $"{Issuer}/auth", ["token_endpoint"] = $"{Issuer}/token",
                ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
            });
        }
        else if (path == $"{Issuer}/jwks")
        {
            var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(_key);
            body = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, crv = jwk.Crv, x = jwk.X, y = jwk.Y, kid = jwk.Kid, alg = "ES256", use = "sig" } } });
        }
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    /// A token as the provider issues it; every argument breaks one rule the Core checks.
    public string Token(string? email = "ana@example.com", bool verified = true, string issuer = Issuer, string audience = ClientId,
        bool expired = false, string? azp = null, object? verifiedClaim = null, ECDsaSecurityKey? signWith = null,
        bool omitVerified = false, string? clientId = null, IDictionary<string, object>? extra = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() };
        if (email is not null) claims["email"] = email;
        if (!omitVerified) claims["email_verified"] = verifiedClaim ?? verified;
        if (clientId is not null) claims["client_id"] = clientId;
        foreach (var (k, v) in extra ?? new Dictionary<string, object>()) claims[k] = v;
        if (azp is not null) claims["azp"] = azp;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims,
            NotBefore = now.AddHours(expired ? -3 : -1), IssuedAt = now.AddHours(expired ? -3 : -1), Expires = now.AddHours(expired ? -2 : 1),
            SigningCredentials = new SigningCredentials(signWith ?? _key, SecurityAlgorithms.EcdsaSha256),
        });
    }
}
