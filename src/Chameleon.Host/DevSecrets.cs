using System.Security.Cryptography;
using System.Text.Json;
using Chameleon.Engine;
using Chameleon.Host.Auth;

namespace Chameleon.Host;

/// Local development only: when CHAMELEON_DEV_SECRETS_FILE is set and the secret key or the CA is
/// not configured, they are generated once into that file (a docker volume) and reused on every
/// start, so nothing secret is versioned and each machine has its own CA. Sealed values depend on
/// the key: deleting the file means resetting the sandboxes (docker compose down -v).
public static class DevSecrets
{
    public const string FileVariable = "CHAMELEON_DEV_SECRETS_FILE";

    private sealed record Stored(string SecretKey, string CaCert, string CaKey);

    public static void Apply(ConfigurationManager config)
    {
        // D7: a deployment with authentication brings its own key and CA.
        if (AuthSettings.From(config).Mode == AuthMode.Oidc) return;
        if (config[FileVariable] is not { Length: > 0 } path) return;
        if (config[SecretBox.KeyVariable] is { Length: > 0 } && config[CertificateAuthority.CertVariable] is { Length: > 0 }) return;

        Stored stored;
        if (File.Exists(path))
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path))
                     ?? throw new InvalidOperationException($"{FileVariable}: invalid file at {path}");
        else
        {
            var (cert, key) = CertificateAuthority.CreateRoot($"Chameleon Dev CA {Environment.MachineName}");
            stored = new Stored(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), cert, key);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }

        config[SecretBox.KeyVariable] ??= stored.SecretKey;
        config[CertificateAuthority.CertVariable] ??= stored.CaCert;
        config[CertificateAuthority.KeyVariable] ??= stored.CaKey;
    }
}
