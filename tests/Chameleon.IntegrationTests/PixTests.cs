using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Chameleon.IntegrationTests;

/// The generic Pix connector: the Central Bank API under /pix/v2, OAuth and webhook transport left to the PSP.
public sealed class PixTests
{
    private const string Key = "00000000-0000-4000-8000-000000000001";
    private const string Hook = "http://app-under-test.local/webhooks/pix";

    [Fact]
    public async Task Subscription_TokenUrl_IssuesToken()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var qa = factory.CreateClient();
        qa.BaseAddress = new Uri(qa.BaseAddress!, "/s/qa/");
        var issued = await TestAuth.Subscribe(qa, "pix");
        Assert.Equal(issued["BASE_URL"] + "/oauth/token", issued["TOKEN_URL"]);

        var request = new HttpRequestMessage(HttpMethod.Post, issued["TOKEN_URL"])
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{issued["CLIENT_ID"]}:{issued["CLIENT_SECRET"]}")));

        (await qa.SendAsync(request)).AssertStatus(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_BasicClient_GrantsRequestedKnownScopes()
    {
        using var factory = new ChameleonFactory();
        var request = new HttpRequestMessage(HttpMethod.Post, "pix/oauth/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", "cob.read cob.write foo")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("client:secret")));

        var resp = await factory.CreateClient().SendAsync(request);

        resp.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("cob.read cob.write", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Token_FormClient_WithoutScope_GrantsAll()
    {
        using var factory = new ChameleonFactory();

        var resp = await Token(factory.CreateClient(), null);

        resp.AssertStatus(HttpStatusCode.OK);
        var scope = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scope").GetString()!.Split(' ');
        Assert.Contains("cobv.write", scope);
        Assert.Contains("webhook.write", scope);
        Assert.Equal(8, scope.Length);
    }

    [Fact]
    public async Task Token_UnknownScopesOnly_Returns400InvalidScope()
    {
        using var factory = new ChameleonFactory();

        var resp = await Token(factory.CreateClient(), "foo bar");

        resp.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal("invalid_scope", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cob_PutGet_RoundTrip()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, null);
        var txid = NewTxId();

        (await http.PutAsJsonAsync($"pix/v2/cob/{txid}", CobBody())).AssertStatus(HttpStatusCode.Created);

        var cob = await http.GetFromJsonAsync<JsonElement>($"pix/v2/cob/{txid}");
        Assert.Equal("ATIVA", cob.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(cob.GetProperty("pixCopiaECola").GetString()));
    }

    [Fact]
    public async Task Cob_WithoutScope_Returns403()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, "pix.read");

        (await http.PutAsJsonAsync($"pix/v2/cob/{NewTxId()}", CobBody())).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CobV_PutGet_UsesCobvScopes()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, "cobv.write cobv.read");
        var txid = NewTxId();

        (await http.PutAsJsonAsync($"pix/v2/cobv/{txid}", new
        {
            calendario = new { dataDeVencimento = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd"), validadeAposVencimento = 30 },
            devedor = new { cpf = "12345678909", nome = "Joao Silva" },
            valor = new { original = "50.00" },
            chave = Key,
        })).AssertStatus(HttpStatusCode.Created);

        (await http.GetAsync($"pix/v2/cobv/{txid}")).AssertStatus(HttpStatusCode.OK);
        (await http.PutAsJsonAsync($"pix/v2/cob/{NewTxId()}", CobBody())).AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Pay_Action_SendsWebhookWithPixList()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, null);
        (await http.PutAsJsonAsync($"pix/v2/webhook/{Key}", new { webhookUrl = Hook })).EnsureSuccessStatusCode();
        var txid = NewTxId();
        await http.PutAsJsonAsync($"pix/v2/cob/{txid}", CobBody());

        (await http.PostAsync($"_chameleon/connectors/pix/entities/cob/{txid}/actions/pay", null)).AssertStatus(HttpStatusCode.OK);

        await factory.SettledAsync();
        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(Hook, url);
        Assert.Equal(txid, JsonDocument.Parse(body).RootElement.GetProperty("pix")[0].GetProperty("txid").GetString());
    }

    [Fact]
    public async Task Pix_Lookup_ByE2eId_AfterPay()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, null);
        var (txid, e2e) = await Paid(http);

        var pix = await http.GetFromJsonAsync<JsonElement>($"pix/v2/pix/{e2e}");

        Assert.Equal(e2e, pix.GetProperty("endToEndId").GetString());
        Assert.Equal(txid, pix.GetProperty("txid").GetString());
    }

    [Fact]
    public async Task Devolucao_PutGet()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, null);
        var (_, e2e) = await Paid(http);
        var id = Guid.NewGuid().ToString("N");

        (await http.PutAsJsonAsync($"pix/v2/pix/{e2e}/devolucao/{id}", new { valor = "5.00" })).AssertStatus(HttpStatusCode.Created);

        var devolucao = await http.GetFromJsonAsync<JsonElement>($"pix/v2/pix/{e2e}/devolucao/{id}");
        Assert.Equal("5.00", devolucao.GetProperty("valor").GetString());
        Assert.Equal(id, devolucao.GetProperty("id").GetString());
    }

    [Fact] // D2: a local receiver is plain http
    public async Task Webhook_HttpUrl_Accepted()
    {
        using var factory = new ChameleonFactory();
        var http = await Authorized(factory, null);

        (await http.PutAsJsonAsync($"pix/v2/webhook/{Key}", new { webhookUrl = Hook })).AssertStatus(HttpStatusCode.NoContent);
        (await http.PutAsJsonAsync($"pix/v2/webhook/{Key}", new { webhookUrl = "ftp://x" })).AssertStatus(HttpStatusCode.BadRequest);
    }

    private static async Task<(string Txid, string E2e)> Paid(HttpClient http)
    {
        var txid = NewTxId();
        await http.PutAsJsonAsync($"pix/v2/cob/{txid}", CobBody());
        (await http.PostAsync($"_chameleon/connectors/pix/entities/cob/{txid}/actions/pay", null)).EnsureSuccessStatusCode();
        var cob = await http.GetFromJsonAsync<JsonElement>($"pix/v2/cob/{txid}");
        return (txid, cob.GetProperty("pix")[0].GetProperty("endToEndId").GetString()!);
    }

    private static object CobBody() => new
    {
        calendario = new { expiracao = 3600 },
        devedor = new { cpf = "12345678909", nome = "Joao Silva" },
        valor = new { original = "20.00" },
        chave = Key,
    };

    private static string NewTxId() => "chameleon" + Guid.NewGuid().ToString("N")[..22];

    private static Task<HttpResponseMessage> Token(HttpClient http, string? scope)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials"), new("client_id", "tests"), new("client_secret", "s") };
        if (scope is not null) form.Add(new("scope", scope));
        return http.PostAsync("pix/oauth/token", new FormUrlEncodedContent(form));
    }

    private static async Task<HttpClient> Authorized(ChameleonFactory factory, string? scope)
    {
        var http = factory.CreateClient();
        var token = await Token(http, scope);
        token.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        return http;
    }
}
