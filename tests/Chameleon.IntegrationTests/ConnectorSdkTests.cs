using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Connector SDK: lifecycle, descriptor and generic actions.
public sealed class ConnectorSdkTests
{
    [Fact]
    public async Task Action_moves_state_records_transition_and_queues_webhook()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        var paid = await http.PostAsync($"/_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        await factory.SettledAsync();
        Assert.Single(factory.Webhooks.Received);

        var transitions = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "StateTransition" });
        var last = transitions.GetProperty("matches")[1];
        Assert.Equal("cob", last.GetProperty("entityType").GetString());
        Assert.Equal("ATIVA", last.GetProperty("fromState").GetString());
        Assert.Equal("CONCLUIDA", last.GetProperty("toState").GetString());

        var created = transitions.GetProperty("matches")[0];
        Assert.Equal(JsonValueKind.Null, created.GetProperty("fromState").ValueKind);
        Assert.Equal("criada -> ATIVA", created.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Invalid_action_is_409_and_changes_nothing()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await http.PostAsync($"/_chameleon/connectors/bradesco/entities/cob/{txid}/actions/remove", null);

        var pay = await http.PostAsync($"/_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null);
        Assert.Equal(HttpStatusCode.Conflict, pay.StatusCode);
        Assert.Contains("REMOVIDA_PELO_USUARIO_RECEBEDOR", (await pay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var fetched = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.Equal("REMOVIDA_PELO_USUARIO_RECEBEDOR", fetched.GetProperty("status").GetString());
        Assert.Empty(factory.Webhooks.Received);

        // The refused call is on record (409); no transition or webhook came out of it.
        var calls = await Verify(http, new { connector = "bradesco", entityId = txid, statusCode = 409 });
        Assert.Equal(1, calls.GetProperty("count").GetInt32());
        var transitions = await Verify(http, new { connector = "bradesco", entityId = txid, kind = "StateTransition" });
        Assert.Equal(2, transitions.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Unknown_action_or_entity_is_404()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync("/_chameleon/connectors/bradesco/entities/cob/does-not-exist/actions/pay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync("/_chameleon/connectors/bradesco/entities/cob/x/actions/explode", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync("/_chameleon/connectors/ninguem/entities/cob/x/actions/pay", null)).StatusCode);
    }

    [Fact]
    public async Task Descriptor_lists_entities_states_actions_and_routes()
    {
        using var factory = new ChameleonFactory();
        var connectors = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/_chameleon/connectors");

        var bradesco = connectors.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "bradesco");
        Assert.Equal("Chameleon.Mock.Connectors.Bradesco", bradesco.GetProperty("packageId").GetString());
        Assert.Equal("0.1.0", bradesco.GetProperty("version").GetString());
        Assert.True(bradesco.GetProperty("verified").GetBoolean());
        var cob = bradesco.GetProperty("entities")[0];
        Assert.Equal("cob", cob.GetProperty("type").GetString());
        Assert.Equal("ATIVA", cob.GetProperty("initial").GetString());
        Assert.Contains("REMOVIDA_PELO_PSP", cob.GetProperty("states").EnumerateArray().Select(s => s.GetString()));

        var pay = bradesco.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "pay");
        Assert.Equal(["ATIVA"], pay.GetProperty("allowedFrom").EnumerateArray().Select(s => s.GetString()!).ToArray());
        Assert.False(pay.TryGetProperty("run", out _));

        var routes = bradesco.GetProperty("routes").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("PUT /bradesco/v2/cob/{txid}", routes);
        Assert.Contains("PUT /bradesco/v2/webhook/{chave}", routes);

        Assert.Equal("banking", bradesco.GetProperty("category").GetString());
        Assert.Contains(bradesco.GetProperty("credentials").EnumerateArray(), c => c.GetProperty("env").GetString() == "CLIENT_SECRET");
        Assert.True(bradesco.GetProperty("hasTestEvent").GetBoolean());

        var nfse = connectors.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "nfse-sp");
        var callback = Assert.Single(nfse.GetProperty("settings").EnumerateArray());
        Assert.True(callback.GetProperty("required").GetBoolean());
        var reject = nfse.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "reject");
        Assert.Equal("reason", reject.GetProperty("parameters")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Connector_logo_is_served_from_its_assembly()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();

        var logo = await http.GetAsync("/_chameleon/connectors/bradesco/logo");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/_chameleon/connectors/nfse-sp/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/connectors/nenhum/logo")).StatusCode);
    }

    [Fact]
    public async Task New_connector_shows_up_without_engine_or_host_changes()
    {
        using var factory = new ChameleonFactory(s => s.AddConnector<DoorConnector>());
        var http = factory.CreateClient();

        var connectors = await http.GetFromJsonAsync<JsonElement>("/_chameleon/connectors");
        Assert.Contains(connectors.EnumerateArray(), c => c.GetProperty("name").GetString() == "door");

        Assert.Equal(HttpStatusCode.OK, (await http.PutAsync("/door/d1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/_chameleon/connectors/door/entities/door/d1/actions/open", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/_chameleon/connectors/door/entities/door/d1/actions/open", null)).StatusCode);

        // Capture and fault injection apply with no code in the connector.
        (await http.PostAsJsonAsync("/_chameleon/faults", new { connector = "door", route = "PUT /door/{id}", kind = "Error", status = 502 }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadGateway, (await http.PutAsync("/door/d2", null)).StatusCode);

        var calls = await Verify(http, new { connector = "door", kind = "InboundRequest" });
        Assert.Equal(2, calls.GetProperty("count").GetInt32());

        // Its entities are browsable through the control API too.
        var door = await http.GetFromJsonAsync<JsonElement>("/_chameleon/connectors/door/entities/door/d1");
        Assert.Equal("Open", door.GetProperty("state").GetString());
        Assert.Empty(door.GetProperty("allowedActions").EnumerateArray());
    }

    [Fact]
    public async Task Legacy_trigger_routes_still_work()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        var rps = "RPS-" + Guid.NewGuid().ToString("N")[..8];
        (await http.PostAsJsonAsync("/nfse-sp/nfse", new { valor = 10, webhookUrl = "https://app.local/nfse", rps })).EnsureSuccessStatusCode();

        var rejected = await http.PostAsJsonAsync($"/_chameleon/nfse-sp/nfse/{rps}/reject", new { reason = "invalid CNPJ" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("invalid CNPJ", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("motivo").GetString());

        // Same action through the generic route: already rejected, so it conflicts.
        Assert.Equal(HttpStatusCode.Conflict,
            (await http.PostAsync($"/_chameleon/connectors/nfse-sp/entities/nfse/{rps}/actions/authorize", null)).StatusCode);
    }

    public enum DoorState { Closed, Open }

    public sealed class Door : Chameleon.Engine.IHasNaturalKey
    {
        public required string Id { get; init; }
        public string NaturalKeyProperty => nameof(Id);
        public DoorState State { get; set; }
    }

    /// A connector written only against the SDK: no engine or host code knows it.
    private sealed class DoorConnector : IConnector
    {
        private static readonly Lifecycle<Door, DoorState> Life =
            new Lifecycle<Door, DoorState>("door", DoorState.Closed, d => d.Id, d => d.State, (d, s) => d.State = s)
                .Allow(DoorState.Closed, DoorState.Open, "open");

        private static readonly ConnectorAction Open = Life.Action("open", "Abrir", (ctx, door, _) =>
        {
            ctx.Move(Life, door, DoorState.Open);
            return Task.FromResult(Results.Ok());
        });

        public string Name => "door";

        public ConnectorDescriptor Describe() => new(Name, [Life.Describe("id")], [Open]);

        public void MapRoutes(IEndpointRouteBuilder routes) =>
            routes.MapPut("/door/{id}", (string id, ConnectorContext ctx) =>
            {
                ctx.Create(Life, new Door { Id = id });
                return Results.Ok();
            });
    }
}
