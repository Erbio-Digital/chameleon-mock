using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// What the panel reads from the control API.
public sealed class ControlApiTests
{
    [Fact]
    public async Task Paused_feed_resumes_in_order_with_after_seq()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        var pausedAt = (await Page(http, "limit=1")).GetProperty("items")[0].GetProperty("seq").GetInt64();

        for (var i = 0; i < 10; i++)
            await http.GetAsync($"/bradesco/v2/cob/{txid}?n={i}");

        var resumed = (await Page(http, $"afterSeq={pausedAt}&kind=InboundRequest&limit=500")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"/bradesco/v2/cob/{txid}?n={i}"), resumed.Select(r => r.GetProperty("path").GetString()));
        var seqs = resumed.Select(r => r.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.Order(), seqs);

        // Paging back: newest first, with the cursor for the next page.
        var first = await Page(http, "kind=InboundRequest&limit=3");
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal($"/bradesco/v2/cob/{txid}?n=9", items[0].GetProperty("path").GetString());
        var next = first.GetProperty("nextBeforeSeq").GetInt64();
        var second = (await Page(http, $"kind=InboundRequest&limit=3&beforeSeq={next}")).GetProperty("items")[0];
        Assert.Equal($"/bradesco/v2/cob/{txid}?n=6", second.GetProperty("path").GetString());
    }

    [Fact] // Feed "Entidade" filter: a call whose route does not carry the key still belongs to the entity it created
    public async Task Entity_filter_finds_the_call_that_created_the_entity()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await InterTests.Sandbox(factory, "ci-entity-filter");
        var created = await http.PostAsJsonAsync("inter/pix/v2/cob",
            new { calendario = new { expiracao = 3600 }, valor = new { original = "10.00" }, chave = "inter-pix@example.com" });
        created.EnsureSuccessStatusCode();
        var txid = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("txid").GetString()!;
        await http.GetAsync($"inter/pix/v2/cob/{txid}");
        (await http.PostAsJsonAsync("inter/pix/v2/cob", new { valor = new { original = "20.00" }, chave = "inter-pix@example.com" }))
            .EnsureSuccessStatusCode(); // another entity: must not show up

        var calls = (await http.GetFromJsonAsync<JsonElement>($"_chameleon/interactions?entityId={txid}&kind=InboundRequest"))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("summary").GetString()).ToList();
        Assert.Equal([$"GET /inter/pix/v2/cob/{txid}", "POST /inter/pix/v2/cob"], calls);
    }

    [Fact]
    public async Task Interaction_detail_by_id()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await CreateCob(http, NewTxId());
        var id = (await Page(http, "limit=1")).GetProperty("items")[0].GetProperty("id").GetString();

        var detail = await http.GetFromJsonAsync<JsonElement>($"/_chameleon/interactions/{id}");
        Assert.Equal(id, detail.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/interactions/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/s/qa/_chameleon/interactions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Entity_detail_has_state_timeline_and_allowed_actions()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        var active = await http.GetFromJsonAsync<JsonElement>($"/_chameleon/connectors/bradesco/entities/cob/{txid}");
        Assert.Equal("ATIVA", active.GetProperty("state").GetString());
        Assert.Equal(["pay", "remove"], active.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        var timeline = active.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("summary").GetString()).ToList();
        Assert.Equal([$"PUT /bradesco/v2/cob/{txid}", "criada -> ATIVA"], timeline.Take(2));

        await http.PostAsync($"/_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null);
        await factory.SettledAsync();
        var paid = await http.GetFromJsonAsync<JsonElement>($"/_chameleon/connectors/bradesco/entities/cob/{txid}");
        Assert.Equal("CONCLUIDA", paid.GetProperty("state").GetString());
        Assert.Empty(paid.GetProperty("allowedActions").EnumerateArray());
        Assert.Equal("Delivered", paid.GetProperty("webhooks")[0].GetProperty("status").GetString());
        Assert.Contains(paid.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("kind").GetString() == "OutboundWebhook");
    }

    [Fact]
    public async Task Entity_data_is_masked()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);

        var list = await http.GetFromJsonAsync<JsonElement>("/_chameleon/connectors/bradesco/entities/cob");
        var cob = list.EnumerateArray().Single(e => e.GetProperty("key").GetString() == txid);
        Assert.Equal("ATIVA", cob.GetProperty("state").GetString());
        var devedor = cob.GetProperty("data").GetProperty("devedor");
        Assert.NotEqual("12345678909", devedor.GetProperty("cpf").GetString());
        Assert.NotEqual("Joao Silva", devedor.GetProperty("nome").GetString());
        Assert.Equal(123.45m, cob.GetProperty("data").GetProperty("valorOriginal").GetDecimal());
    }

    [Fact]
    public async Task Unknown_connector_or_entity_type_is_404()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/connectors/ninguem/entities/cob")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/connectors/bradesco/entities/boleto")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/connectors/bradesco/entities/cob/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Armed_faults_are_listed()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa");
        var http = factory.CreateClient();
        (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Timeout", times = 2 })).EnsureSuccessStatusCode();

        var armed = Assert.Single((await http.GetFromJsonAsync<JsonElement>("/_chameleon/faults")).EnumerateArray().ToList());
        Assert.Equal(("Timeout", 2), (armed.GetProperty("kind").GetString(), armed.GetProperty("remaining").GetInt32()));
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("/s/qa/_chameleon/faults")).EnumerateArray());
    }

    private static Task<JsonElement> Page(HttpClient http, string query) =>
        http.GetFromJsonAsync<JsonElement>($"/_chameleon/interactions?{query}");
}
