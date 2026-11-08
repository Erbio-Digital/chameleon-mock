using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Engine;

/// Personal access token an operator creates for the MCP (role AGENT). Only the hash is stored.
public sealed class AccessToken
{
    public const string Prefix = "chm_pat_";

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string OwnerEmail { get; init; }
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// A new token: the value goes to the operator once, the hash to the store.
    public static (string Token, string Hash) Generate()
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// Persistence port for personal access tokens (in-memory, Postgres).
public interface IAccessTokenStore
{
    void Add(AccessToken token);
    AccessToken? FindByHash(string tokenHash);
    IReadOnlyList<AccessToken> List(string ownerEmail);
    /// False when the token does not exist or belongs to someone else.
    bool Revoke(string ownerEmail, Guid id, DateTimeOffset utcNow);
    void Touch(Guid id, DateTimeOffset utcNow);
}

public sealed class InMemoryAccessTokenStore : IAccessTokenStore
{
    private readonly List<AccessToken> _tokens = new();
    private readonly object _gate = new();

    public void Add(AccessToken token)
    {
        lock (_gate) _tokens.Add(token);
    }

    public AccessToken? FindByHash(string tokenHash)
    {
        lock (_gate) return _tokens.FirstOrDefault(t => t.TokenHash == tokenHash);
    }

    public IReadOnlyList<AccessToken> List(string ownerEmail)
    {
        lock (_gate) return _tokens.Where(t => t.OwnerEmail == ownerEmail).OrderBy(t => t.CreatedAt).ToList();
    }

    public bool Revoke(string ownerEmail, Guid id, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            if (_tokens.FirstOrDefault(t => t.Id == id && t.OwnerEmail == ownerEmail) is not { } token) return false;
            token.RevokedAt ??= utcNow;
            return true;
        }
    }

    public void Touch(Guid id, DateTimeOffset utcNow)
    {
        lock (_gate)
            if (_tokens.FirstOrDefault(t => t.Id == id) is { } token) token.LastUsedAt = utcNow;
    }
}
