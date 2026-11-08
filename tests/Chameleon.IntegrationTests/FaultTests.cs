using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Faults are a generic route filter.
[Collection(Serial.Name)]
public sealed class FaultTests
{
    private const string PutCob = "PUT /bradesco/v2/cob/{txid}";
    private const string GetCob = "GET /bradesco/v2/cob/{txid}";

    [Fact]
    public async Task Armed_fault_hits_target_route_once_and_is_journaled()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await Arm(http, PutCob, new { kind = "Error", status = 503, times = 1 });

        var txid = NewTxId();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(http, txid)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, txid)).StatusCode);

        var faulted = await Verify(http, new { entityId = txid, summaryContains = "[fault:Error]" });
        Assert.Equal(503, faulted.GetProperty("matches")[0].GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task Latency_delays_then_returns_real_response()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await Arm(http, GetCob, new { kind = "Latency", delayMs = 1500 });

        var watch = Stopwatch.StartNew();
        var cob = await http.GetFromJsonAsync<JsonElement>($"/bradesco/v2/cob/{txid}");
        Assert.True(watch.ElapsedMilliseconds >= 1400);
        Assert.Equal("ATIVA", cob.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Malformed_returns_success_with_unparseable_body()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await Arm(http, GetCob, new { kind = "Malformed" });

        var resp = await http.GetAsync($"/bradesco/v2/cob/{txid}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await Assert.ThrowsAnyAsync<JsonException>(() => resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Cleared_fault_does_not_fire()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await Arm(http, PutCob, new { kind = "Timeout" });

        await http.DeleteAsync($"/_chameleon/faults?connector=bradesco&route={Uri.EscapeDataString(PutCob)}");

        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, NewTxId())).StatusCode);
    }

    [Fact]
    public async Task Unknown_route_is_rejected_with_known_routes()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/_chameleon/faults", new { connector = "bradesco", route = "PUT /v2/cob", kind = "Error" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var routes = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routes")
            .EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains(PutCob, routes);
        Assert.DoesNotContain(routes, r => r!.Contains("/_chameleon"));
    }

    [Fact]
    public async Task Timed_fault_hits_every_call_until_the_sandbox_clock_passes_it()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = PutCob, kind = "Error", times = 2, durationSeconds = 60 })).StatusCode);
        await Arm(http, PutCob, new { kind = "Error", status = 503, durationSeconds = 600 });

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(http, NewTxId())).StatusCode);
        var armed = (await http.GetFromJsonAsync<JsonElement>("/_chameleon/faults"))[0];
        Assert.Equal(JsonValueKind.Null, armed.GetProperty("remaining").ValueKind);
        Assert.Equal(JsonValueKind.String, armed.GetProperty("until").ValueKind);

        await http.PostAsJsonAsync("/_chameleon/clock/advance", new { seconds = 600 });
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(http, NewTxId())).StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("/_chameleon/faults")).EnumerateArray());
    }

    [Fact]
    public async Task Connector_wide_fault_hits_every_route_and_the_exact_route_wins()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        var txid = NewTxId();
        await CreateCob(http, txid);
        await Arm(http, "*", new { kind = "Error", status = 503, durationSeconds = 600 });
        await Arm(http, PutCob, new { kind = "Error", status = 429, times = 1 });

        Assert.Equal(HttpStatusCode.TooManyRequests, (await CreateCob(http, NewTxId())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(http, NewTxId())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync($"/bradesco/v2/cob/{txid}")).StatusCode);
        var armed = Assert.Single((await http.GetFromJsonAsync<JsonElement>("/_chameleon/faults")).EnumerateArray());
        Assert.Equal("*", armed.GetProperty("route").GetString());

        // Webhook and settlement are not request routes: those kinds stay on their own routes.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "*", kind = "Hold" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "does-not-exist", route = "*", kind = "Error" })).StatusCode);

        await http.DeleteAsync("/_chameleon/faults?connector=bradesco&route=*");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/bradesco/v2/cob/{txid}")).StatusCode);
    }

    private static async Task Arm(HttpClient http, string route, object fault)
    {
        var body = JsonSerializer.SerializeToNode(fault)!.AsObject();
        body["connector"] = "bradesco";
        body["route"] = route;
        (await http.PostAsJsonAsync("/_chameleon/faults", body)).EnsureSuccessStatusCode();
    }
}
