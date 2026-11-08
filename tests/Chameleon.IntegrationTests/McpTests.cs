using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// The MCP server at /_chameleon/mcp, driven by the official MCP client.
[Collection(Serial.Name)]
public sealed class McpTests
{
    [Fact]
    public async Task Lists_prefixed_tools_with_annotations()
    {
        using var factory = new ChameleonFactory();
        await using var mcp = await Connect(factory);

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.All(tools.Keys, name => Assert.StartsWith("chameleon_", name));
        Assert.Equal(35, tools.Count);
        Assert.True(tools["chameleon_list_sandboxes"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["chameleon_reset_sandbox"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.Contains("describe_connectors", mcp.ServerInstructions);
    }

    [Fact] // the setup a developer or tester does for a test, end to end through the agent
    public async Task Sandbox_fault_action_history_and_webhook_through_tools()
    {
        using var factory = new ChameleonFactory();
        await using var mcp = await Connect(factory);

        await Call(mcp, "chameleon_create_sandbox", new() { ["id"] = "ci-mcp" });
        var http = await WebhookOutboxTests.SandboxClient(factory, "ci-mcp");

        // Armed through MCP, felt by the app.
        await Call(mcp, "chameleon_arm_fault", new()
        {
            ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["route"] = "PUT /bradesco/v2/cob/{txid}", ["kind"] = "error", ["status"] = 503,
        });
        var txid = NewTxId();
        Assert.Equal(503, (int)(await PutCob(http, txid)).StatusCode);
        Assert.Empty((await Call(mcp, "chameleon_list_faults", new() { ["sandbox"] = "ci-mcp" })).EnumerateArray());

        await CreateCob(http, txid);
        var active = await Call(mcp, "chameleon_list_entities", new() { ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["entityType"] = "cob", ["state"] = "ATIVA" });
        Assert.Equal(txid, active.GetProperty("items")[0].GetProperty("key").GetString());

        var paid = await Call(mcp, "chameleon_run_action", new()
        {
            ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["entityType"] = "cob", ["key"] = txid, ["action"] = "pay",
        });
        Assert.Equal("CONCLUIDA", paid.GetProperty("state").GetString());
        await factory.SettledAsync("ci-mcp");

        var history = await Call(mcp, "chameleon_search_interactions", new() { ["sandbox"] = "ci-mcp", ["entityId"] = txid, ["statusCode"] = 503 });
        Assert.Equal(1, history.GetProperty("count").GetInt32());
        var webhooks = await Call(mcp, "chameleon_list_webhooks", new() { ["sandbox"] = "ci-mcp", ["entityId"] = txid });
        Assert.Equal("Delivered", webhooks.GetProperty("items")[0].GetProperty("status").GetString());

        var entity = await Call(mcp, "chameleon_get_entity", new() { ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["entityType"] = "cob", ["key"] = txid });
        Assert.DoesNotContain("12345678909", entity.ToString()); // masked

        await Call(mcp, "chameleon_set_webhook_registration", new()
        {
            ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["key"] = "chave-mcp", ["url"] = "https://app-under-test.local/mcp",
        });
        async Task<List<string?>> Keys() => (await Call(mcp, "chameleon_list_webhook_registrations", new() { ["sandbox"] = "ci-mcp" }))
            .EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToList();
        Assert.Contains("chave-mcp", await Keys());
        await Call(mcp, "chameleon_remove_webhook_registration", new() { ["sandbox"] = "ci-mcp", ["connector"] = "bradesco", ["key"] = "chave-mcp" });
        Assert.DoesNotContain("chave-mcp", await Keys());

        // Subscription through the agent: the issued credentials come back.
        await Call(mcp, "chameleon_unsubscribe_connector", new() { ["sandbox"] = "ci-mcp", ["connector"] = "bradesco" });
        var subscribed = await Call(mcp, "chameleon_subscribe_connector", new() { ["sandbox"] = "ci-mcp", ["connector"] = "bradesco" });
        Assert.Contains(subscribed.GetProperty("credentials").EnumerateArray(), c => c.GetProperty("env").GetString() == "CLIENT_SECRET");
        // The client certificate comes back without its private key: that stays with the panel.
        var certificate = subscribed.GetProperty("certificate");
        Assert.Contains("CERTIFICATE", certificate.GetProperty("pem").GetString());
        Assert.False(certificate.TryGetProperty("keyPem", out _));
        Assert.False(certificate.TryGetProperty("pfxPassword", out _));
        await Call(mcp, "chameleon_subscribe_connector", new() { ["sandbox"] = "ci-mcp", ["connector"] = "nfse-sp" });
        var configured = await Call(mcp, "chameleon_configure_connector", new()
        {
            ["sandbox"] = "ci-mcp", ["connector"] = "nfse-sp",
            ["settings"] = new Dictionary<string, string?> { ["callbackUrl"] = "https://app-under-test.local/nfse" },
        });
        Assert.Equal("Active", configured.GetProperty("status").GetString());
        await Call(mcp, "chameleon_send_test_event", new() { ["sandbox"] = "ci-mcp", ["connector"] = "nfse-sp" });

        await Call(mcp, "chameleon_delete_sandbox", new() { ["sandbox"] = "ci-mcp" });
    }

    [Fact] // The agent cannot reset a permanent sandbox
    public async Task Permanent_sandbox_reset_is_refused()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        await using var mcp = await Connect(factory);

        var refused = await mcp.CallToolAsync("chameleon_reset_sandbox", new Dictionary<string, object?> { ["sandbox"] = "qa" });
        Assert.True(refused.IsError);
        Assert.Contains("panel", Text(refused));
    }

    [Fact]
    public async Task Creates_permanent_sandbox_and_moves_its_clock_back()
    {
        using var factory = new ChameleonFactory();
        await using var mcp = await Connect(factory);

        var created = await Call(mcp, "chameleon_create_sandbox", new() { ["id"] = "team", ["permanent"] = true });
        Assert.True(created.GetProperty("persistent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("expiresAt").ValueKind);
        Assert.True((await mcp.CallToolAsync("chameleon_reset_sandbox", new Dictionary<string, object?> { ["sandbox"] = "team" })).IsError);
        Assert.True((await mcp.CallToolAsync("chameleon_delete_sandbox", new Dictionary<string, object?> { ["sandbox"] = "team" })).IsError);

        var before = created.GetProperty("now").GetDateTimeOffset();
        var moved = await Call(mcp, "chameleon_advance_clock", new() { ["sandbox"] = "team", ["seconds"] = -60 });
        Assert.Equal(before.AddSeconds(-60), moved.GetProperty("now").GetDateTimeOffset());
    }

    [Fact]
    public async Task Api_errors_come_back_readable()
    {
        using var factory = new ChameleonFactory();
        await using var mcp = await Connect(factory);
        var txid = NewTxId();
        await CreateCob(await AuthorizedClient(factory), txid);
        var args = new Dictionary<string, object?>
        {
            ["sandbox"] = "default", ["connector"] = "bradesco", ["entityType"] = "cob", ["key"] = txid, ["action"] = "pay",
        };
        await mcp.CallToolAsync("chameleon_run_action", args);

        var again = await mcp.CallToolAsync("chameleon_run_action", args);
        Assert.True(again.IsError);
        Assert.Contains("409", Text(again));
        Assert.Contains("CONCLUIDA", Text(again));

        var badKind = await mcp.CallToolAsync("chameleon_arm_fault", new Dictionary<string, object?>
        {
            ["sandbox"] = "default", ["connector"] = "bradesco", ["route"] = "webhook", ["kind"] = "explode",
        });
        Assert.Contains("Duplicate", Text(badKind));
    }

    private static async Task<McpClient> Connect(ChameleonFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/_chameleon/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> Call(McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        var result = await mcp.CallToolAsync(tool, args);
        Assert.True(result.IsError is not true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().First().Text;

    private static Task<HttpResponseMessage> PutCob(HttpClient http, string txid) =>
        http.PutAsJsonAsync($"bradesco/v2/cob/{txid}", new { calendario = new { expiracao = 3600 }, valor = new { original = "1.00" }, chave = "k" });
}
