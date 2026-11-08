using Chameleon.Host.Auth;
using Microsoft.Extensions.Configuration;

namespace Chameleon.IntegrationTests.Auth;

public sealed class AuthStartupTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static readonly (string, string)[] Complete =
    [
        ("CHAMELEON_AUTH", "oidc"), ("CHAMELEON_OIDC_ISSUER", "https://idp.example.com"), ("CHAMELEON_OIDC_CLIENT_ID", "panel"),
        ("CHAMELEON_ALLOWED_EMAIL_DOMAINS", "example.com"), ("CHAMELEON_WEBHOOK_ALLOWED_HOSTS", "localhost"),
        ("CHAMELEON_SECRET_KEY", ChameleonFactory.SecretKey), ("CHAMELEON_CA_CERT", "x"), ("CHAMELEON_CA_KEY", "x"),
    ];

    [Fact]
    public void None_is_the_default() => Assert.Equal(AuthMode.None, AuthSettings.From(Config()).Mode);

    [Fact]
    public void Unknown_mode_does_not_start()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthSettings.From(Config(("CHAMELEON_AUTH", "supa"))));
        Assert.Contains("CHAMELEON_AUTH", error.Message);
    }

    [Fact]
    public void Oidc_lists_every_missing_setting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ChameleonAuth.Validate(Config(("CHAMELEON_AUTH", "oidc"))));
        foreach (var key in new[] { "CHAMELEON_OIDC_ISSUER", "CHAMELEON_OIDC_CLIENT_ID", "CHAMELEON_ALLOWED_EMAIL_DOMAINS or CHAMELEON_ALLOWED_EMAILS",
                     "CHAMELEON_WEBHOOK_ALLOWED_HOSTS", "CHAMELEON_SECRET_KEY", "CHAMELEON_CA_CERT", "CHAMELEON_CA_KEY" })
            Assert.Contains(key, error.Message);
    }

    [Fact]
    public void Oidc_without_allowlist_does_not_start()
    {
        var values = Complete.Where(v => v.Item1 != "CHAMELEON_ALLOWED_EMAIL_DOMAINS").ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => ChameleonAuth.Validate(Config(values)));
        Assert.Contains("CHAMELEON_ALLOWED_EMAIL_DOMAINS or CHAMELEON_ALLOWED_EMAILS", error.Message);
    }

    [Fact]
    public void Allowed_emails_alone_are_an_allowlist() =>
        ChameleonAuth.Validate(Config([.. Complete.Where(v => v.Item1 != "CHAMELEON_ALLOWED_EMAIL_DOMAINS"), ("CHAMELEON_ALLOWED_EMAILS", "ana@example.net")]));

    [Fact]
    public void Ci_key_is_optional_but_prefixed()
    {
        ChameleonAuth.Validate(Config(Complete));
        var error = Assert.Throws<InvalidOperationException>(() => ChameleonAuth.Validate(Config([.. Complete, ("CHAMELEON_CI_KEY", "plain")])));
        Assert.Contains("chm_ci_", error.Message);
    }

    [Fact]
    public void Http_issuer_only_on_loopback()
    {
        var remote = Complete.Select(v => v.Item1 == "CHAMELEON_OIDC_ISSUER" ? (v.Item1, "http://idp.example.com") : v).ToArray();
        Assert.Contains("https", Assert.Throws<InvalidOperationException>(() => ChameleonAuth.Validate(Config(remote))).Message);
        ChameleonAuth.Validate(Config(Complete.Select(v => v.Item1 == "CHAMELEON_OIDC_ISSUER" ? (v.Item1, "http://localhost:8180/realms/test") : v).ToArray()));
    }

    [Theory]
    [InlineData("ana@example.com", true)]
    [InlineData("ANA@Example.COM", true)]
    [InlineData("ana@sub.example.com", false)]
    [InlineData("ana@example.net", false)]
    [InlineData("bia@example.net", true)]
    [InlineData("example.com", false)]
    public void Allowlist_matches_domain_or_exact_email(string email, bool expected)
    {
        var settings = AuthSettings.From(Config([.. Complete, ("CHAMELEON_ALLOWED_EMAILS", "bia@example.net")]));
        Assert.Equal(expected, settings.IsAllowed(email));
    }

    [Fact]
    public void Oidc_ignores_the_dev_secrets_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chameleon-dev-{Guid.NewGuid():N}.json");
        var config = new ConfigurationManager();
        config["CHAMELEON_AUTH"] = "oidc";
        config["CHAMELEON_DEV_SECRETS_FILE"] = path;
        Chameleon.Host.DevSecrets.Apply(config);
        Assert.False(File.Exists(path));
        Assert.Null(config["CHAMELEON_CA_CERT"]);
    }
}
