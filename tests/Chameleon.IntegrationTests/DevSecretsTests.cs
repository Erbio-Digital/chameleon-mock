using Microsoft.Extensions.Configuration;
using Chameleon.Engine;
using Chameleon.Host;

namespace Chameleon.IntegrationTests;

/// Local dev secrets: generated once into the file, the same on every later start.
public sealed class DevSecretsTests
{
    [Fact]
    public void Secrets_are_generated_once_and_reused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chameleon-dev-{Guid.NewGuid():N}", "dev-secrets.json");
        try
        {
            string? Start(out string? ca)
            {
                var config = new ConfigurationManager();
                config[DevSecrets.FileVariable] = path;
                DevSecrets.Apply(config);
                _ = new SecretBox(config[SecretBox.KeyVariable]);
                _ = new CertificateAuthority(config[CertificateAuthority.CertVariable], config[CertificateAuthority.KeyVariable]);
                ca = config[CertificateAuthority.CertVariable];
                return config[SecretBox.KeyVariable];
            }

            var first = Start(out var firstCa);
            var second = Start(out var secondCa);
            Assert.Equal(first, second);
            Assert.Equal(firstCa, secondCa);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
