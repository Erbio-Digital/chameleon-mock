using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.IntegrationTests;

/// A connector's WebhookSigner runs on each attempt over the exact body, with the subscription's secret.
public sealed class WebhookSignerTests
{
    private sealed class SignedConnector : IConnector
    {
        public string Name => "signed";

        public ConnectorDescriptor Describe() => new(Name, [], [], new WebhookPolicy(MaxAttempts: 2, BaseDelayMs: 0, TimeoutMs: 5000))
        {
            Credentials = [new("WEBHOOK_SECRET", "Segredo", CredentialKind.Secret)],
            WebhookSigner = input =>
            {
                var ts = input.UtcNow.ToString("O");
                return new Dictionary<string, string>
                {
                    ["x-ts"] = ts,
                    ["x-sig"] = Sign(input.Credential("WEBHOOK_SECRET") ?? "", ts, input.Body),
                };
            },
        };

        public void MapRoutes(IEndpointRouteBuilder routes) =>
            routes.MapPost("/signed/notify", async (ConnectorContext ctx) =>
            {
                await ctx.NotifyAsync("e1", "https://app-under-test.local/hook", new { evento = "ok" });
                return Results.Ok();
            });
    }

    private static string Sign(string secret, string ts, string body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + body)));

    [Fact]
    public async Task Signer_headers_are_computed_per_attempt_with_the_issued_secret()
    {
        using var factory = new ChameleonFactory(s => s.AddConnector<SignedConnector>());
        factory.Webhooks.Respond = n => n == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-signed" })).EnsureSuccessStatusCode();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, "/s/ci-signed/");
        var issued = await TestAuth.Subscribe(http, "signed");

        (await http.PostAsync("signed/notify", null)).EnsureSuccessStatusCode();
        await factory.SettledAsync("ci-signed");

        var bodies = factory.Webhooks.Received.Select(r => r.Body).ToList();
        var headers = factory.Webhooks.ReceivedHeaders.ToList();
        Assert.Equal(2, headers.Count);
        for (var i = 0; i < 2; i++)
            Assert.Equal(Sign(issued["WEBHOOK_SECRET"], headers[i]["x-ts"], bodies[i]), headers[i]["x-sig"]);
        Assert.NotEqual(headers[0]["x-ts"], headers[1]["x-ts"]);
    }
}
