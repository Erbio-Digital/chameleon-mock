using System.Security.Cryptography;
using System.Text;
using Chameleon.Engine;

namespace Chameleon.Sdk;

public enum SubscriptionStatus { Available, Pending, Active }

public enum CertificateSource { Generated, Csr }

/// The client certificate of a subscription: public PEM in clear, the key sealed and only
/// when the Chameleon generated it (a signed CSR keeps the key with the app).
public sealed record IssuedCertificate(string Pem, string? KeySealed, string Thumbprint, DateTimeOffset NotAfter, CertificateSource Source)
{
    public static IssuedCertificate From(string pem, string? keySealed, CertificateSource source)
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        return new(pem, keySealed, CertificateAuthority.Thumbprint(cert), cert.NotAfter.ToUniversalTime(), source);
    }
}

/// A connector subscribed in one sandbox: credentials issued for it and the settings the
/// service under test filled in. Lives in the sandbox store, so a sandbox copy carries it.
/// Secrets are sealed with SecretBox; ids, URLs and settings stay in clear.
public sealed record ConnectorSubscription : IHasNaturalKey
{
    public required string Connector { get; init; }
    public string NaturalKeyProperty => nameof(Connector);

    /// False once cancelled (the store keeps the row): no credential of it works any more.
    public bool Active { get; init; } = true;
    public DateTimeOffset SubscribedAt { get; init; }
    public DateTimeOffset? RotatedAt { get; init; }
    public Dictionary<string, string> Issued { get; init; } = [];
    public Dictionary<string, string> Settings { get; init; } = [];
    public IssuedCertificate? Certificate { get; init; }
    /// Password of the .pfx export, chosen on the first download and sealed; kept across rotations.
    public string? PfxPasswordSealed { get; init; }

    /// New credentials (and, for a connector that requires mTLS, a new generated certificate).
    public static ConnectorSubscription Issue(ConnectorDescriptor d, SecretBox secrets, CertificateAuthority ca, DateTimeOffset now,
        Dictionary<string, string>? settings = null, DateTimeOffset? subscribedAt = null, string? pfxPasswordSealed = null) => new()
    {
        Connector = d.Name,
        PfxPasswordSealed = pfxPasswordSealed,
        Certificate = d.ClientCertificate ? Generate(d, secrets, ca) : null,
        SubscribedAt = subscribedAt ?? now,
        RotatedAt = subscribedAt is null ? null : now,
        Settings = settings ?? [],
        Issued = d.Credentials.Where(c => c.Kind != CredentialKind.Url).ToDictionary(c => c.Env, c => c.Kind == CredentialKind.Secret
            ? secrets.Encrypt(Random(24))
            : $"{d.Name}-{Random(6)}"),
    };

    /// Missing required settings, by label: while any, the connector does not deliver events.
    public IReadOnlyList<string> Missing(ConnectorDescriptor d) =>
        d.Settings.Where(s => s.Required && string.IsNullOrWhiteSpace(Settings.GetValueOrDefault(s.Key))).Select(s => s.Label).ToList();

    public SubscriptionStatus Status(ConnectorDescriptor d) =>
        !Active ? SubscriptionStatus.Available : Missing(d).Count > 0 ? SubscriptionStatus.Pending : SubscriptionStatus.Active;

    /// The issued value in clear (secrets unsealed).
    public string? Value(ConnectorDescriptor d, SecretBox secrets, string env) =>
        Issued.GetValueOrDefault(env) is not { } stored ? null
        : d.Credentials.Any(c => c.Env == env && c.Kind == CredentialKind.Secret) ? secrets.Decrypt(stored) : stored;

    /// Compares in constant time, so a wrong guess takes as long as a right one.
    public bool Matches(ConnectorDescriptor d, SecretBox secrets, string env, string? given) =>
        Active && given is not null && Value(d, secrets, env) is { } expected
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    /// True when the presented certificate is this subscription's.
    public bool Holds(string thumbprint) => Active && Certificate is { } c && string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);

    public static string CommonName(ConnectorDescriptor d) => $"{d.Name}.client.chameleon";

    private static IssuedCertificate Generate(ConnectorDescriptor d, SecretBox secrets, CertificateAuthority ca)
    {
        var (pem, key) = ca.IssueClient(CommonName(d));
        return IssuedCertificate.From(pem, secrets.Encrypt(key), CertificateSource.Generated);
    }

    private static string Random(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
