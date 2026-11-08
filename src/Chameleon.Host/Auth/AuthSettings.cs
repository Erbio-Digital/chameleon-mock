namespace Chameleon.Host.Auth;

public enum AuthMode { None, Oidc }

/// How the instance authenticates, read once from configuration.
public sealed record AuthSettings(AuthMode Mode, string? Issuer, string? ClientId, string? Audience,
    string[] AllowedDomains, string[] AllowedEmails, string? CiKey)
{
    public const string ModeKey = "CHAMELEON_AUTH";
    public const string IssuerKey = "CHAMELEON_OIDC_ISSUER";
    public const string ClientIdKey = "CHAMELEON_OIDC_CLIENT_ID";
    public const string AudienceKey = "CHAMELEON_OIDC_AUDIENCE";
    public const string AllowedDomainsKey = "CHAMELEON_ALLOWED_EMAIL_DOMAINS";
    public const string AllowedEmailsKey = "CHAMELEON_ALLOWED_EMAILS";
    public const string CiKeyKey = "CHAMELEON_CI_KEY";

    public static AuthSettings From(IConfiguration c) => new(
        (c[ModeKey]?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "none" => AuthMode.None,
            "oidc" => AuthMode.Oidc,
            var other => throw new InvalidOperationException($"{ModeKey}: '{other}' is not one of none, oidc"),
        },
        Value(c, IssuerKey)?.TrimEnd('/'), Value(c, ClientIdKey), Value(c, AudienceKey),
        List(c, AllowedDomainsKey).Select(d => d.TrimStart('@')).ToArray(), List(c, AllowedEmailsKey), Value(c, CiKeyKey));

    /// What the "aud" of an accepted JWT must contain.
    public string ExpectedAudience => Audience ?? ClientId!;

    /// Exact e-mail, or user@domain for an allowed domain (no subdomains). Case-insensitive.
    public bool IsAllowed(string email) =>
        AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase)
        || (email.LastIndexOf('@') is var at and > 0 && AllowedDomains.Contains(email[(at + 1)..], StringComparer.OrdinalIgnoreCase));

    private static string? Value(IConfiguration c, string key) => c[key] is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string[] List(IConfiguration c, string key) =>
        (c[key] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
