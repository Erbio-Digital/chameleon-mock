using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chameleon.Engine;

namespace Chameleon.IntegrationTests;

/// Outside "default" a connector must be subscribed and only its issued credentials work.
internal static class TestAuth
{
    /// Subscribes the connector in the sandbox the client points at (no-op when already subscribed)
    /// and returns the issued credentials by env name.
    internal static async Task<Dictionary<string, string>> Subscribe(HttpClient sandboxClient, string connector)
    {
        var created = await sandboxClient.PostAsync($"_chameleon/connectors/{connector}/subscription", null);
        if (created.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            throw new InvalidOperationException($"assinatura falhou: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        var detail = await sandboxClient.GetFromJsonAsync<JsonElement>($"_chameleon/connectors/{connector}/subscription");
        // Present the sandbox's certificate, as the app configured with it would.
        if (detail.TryGetProperty("certificate", out var cert) && cert.ValueKind == JsonValueKind.Object)
            Present(sandboxClient, cert.GetProperty("pem").GetString()!);
        return detail.GetProperty("credentials").EnumerateArray()
            .ToDictionary(c => c.GetProperty("env").GetString()!, c => c.GetProperty("value").GetString()!);
    }

    internal static void Present(HttpClient client, string certificatePem)
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certificatePem);
        client.DefaultRequestHeaders.Remove(ChameleonFactory.ClientCertificateHeader);
        client.DefaultRequestHeaders.Add(ChameleonFactory.ClientCertificateHeader, Convert.ToBase64String(cert.RawData));
    }

    /// Bradesco Pix token as the real API takes it: form grant_type, the client in HTTP Basic.
    internal static Task<HttpResponseMessage> BradescoToken(HttpClient http, string clientId, string? clientSecret = "") =>
        http.SendAsync(BradescoTokenRequest(clientId, clientSecret));

    internal static HttpRequestMessage BradescoTokenRequest(string clientId, string? clientSecret = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "bradesco/v2/oauth/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        return request;
    }

    /// Bearer for Bradesco in the sandbox: issued credentials outside "default", any client in it.
    internal static async Task Bradesco(HttpClient sandboxClient, string sandbox)
    {
        var (id, secret) = ("tests", "");
        if (sandbox != Sandbox.Default)
        {
            var issued = await Subscribe(sandboxClient, "bradesco");
            (id, secret) = (issued["CLIENT_ID"], issued["CLIENT_SECRET"]);
        }
        var token = await BradescoToken(sandboxClient, id, secret);
        token.EnsureSuccessStatusCode();
        var access = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        sandboxClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        // Subscribed, the charge's simulator-only URL no longer counts: register the test key's
        // webhook, as the app does on startup.
        if (sandbox != Sandbox.Default)
            (await sandboxClient.PutAsJsonAsync($"bradesco/v2/webhook/{BradescoPixTests.Chave}", new { webhookUrl = BradescoPixTests.WebhookUrl }))
                .EnsureSuccessStatusCode();
    }
}
