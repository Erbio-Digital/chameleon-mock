using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Sandboxes are isolated, each with its own clock.
public sealed class SandboxTests
{
    [Fact]
    public async Task Entities_and_journal_do_not_leak_between_sandboxes()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-a");
        await CreateSandbox(factory, "sb-b");
        var a = await AuthorizedClient(factory, "sb-a");
        var b = await AuthorizedClient(factory, "sb-b");
        var txid = NewTxId();

        Assert.Equal(HttpStatusCode.Created, (await CreateCob(a, txid)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"bradesco/v2/cob/{txid}")).StatusCode);
        var journalB = await b.GetFromJsonAsync<JsonElement>($"_chameleon/journal?entityId={txid}");
        Assert.All(journalB.EnumerateArray(), e => Assert.Equal("sb-b", e.GetProperty("sandboxId").GetString()));
        Assert.DoesNotContain(journalB.EnumerateArray(), e => e.GetProperty("method").GetString() == "PUT");
    }

    [Fact]
    public async Task Qa_and_hml_keep_separate_charges_with_same_key()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var qa = await AuthorizedClient(factory, "qa");
        var hml = await AuthorizedClient(factory, "hml");
        var txid = NewTxId();

        Assert.Equal(HttpStatusCode.Created, (await CreateCob(qa, txid)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(hml, txid)).StatusCode);

        (await qa.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();

        Assert.Equal("CONCLUIDA", await Status(qa, txid));
        Assert.Equal("ATIVA", await Status(hml, txid));
    }

    [Fact]
    public async Task Call_without_sandbox_lands_in_default()
    {
        using var factory = new ChameleonFactory();
        var http = await BradescoPixTests.AuthorizedClient(factory);
        var txid = NewTxId();

        var resp = await CreateCob(http, txid);
        Assert.Equal("default", resp.Headers.GetValues("X-Chameleon-Sandbox").Single());

        var viaPrefix = await AuthorizedClient(factory, "default");
        Assert.Equal("ATIVA", await Status(viaPrefix, txid));
    }

    [Fact]
    public async Task Header_selects_sandbox_and_unknown_sandbox_is_404()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-header");
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Chameleon-Sandbox", "sb-header");
        var token = await TestAuth.BradescoToken(http, "t");
        Assert.Equal("sb-header", token.Headers.GetValues("X-Chameleon-Sandbox").Single());

        var unknown = await factory.CreateClient().GetAsync("/s/does-not-exist/bradesco/v2/cob/x");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Ephemeral_sandbox_expires_with_its_data()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-ci", ttlSeconds: 1);
        var ci = await AuthorizedClient(factory, "sb-ci");
        await CreateCob(ci, NewTxId());

        var control = factory.CreateClient();
        // An expired sandbox goes away on its next use (and with the retention job).
        await ChameleonFactory.EventuallyAsync(async () =>
            (await control.GetAsync("/s/sb-ci/_chameleon/journal")).StatusCode == HttpStatusCode.NotFound, timeoutMs: 5000);
        Assert.Equal(HttpStatusCode.NotFound, (await control.GetAsync("/_chameleon/sandboxes/sb-ci")).StatusCode);
        // Recreated with the same id, it starts empty: the old data went with it.
        await CreateSandbox(factory, "sb-ci");
        var journal = await control.GetFromJsonAsync<JsonElement>("/s/sb-ci/_chameleon/journal");
        Assert.Equal(0, journal.GetArrayLength());
    }

    [Fact]
    public async Task Persistent_sandbox_reset_requires_confirmation()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var qa = await AuthorizedClient(factory, "qa");
        var txid = NewTxId();
        await CreateCob(qa, txid);

        var control = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await control.PostAsync("/_chameleon/sandboxes/qa/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await control.PostAsync("/_chameleon/sandboxes/qa/reset?confirm=hml", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await control.DeleteAsync("/_chameleon/sandboxes/qa")).StatusCode);

        Assert.Equal("ATIVA", await Status(qa, txid));
    }

    [Fact]
    public async Task Seed_creates_only_default()
    {
        using var factory = new ChameleonFactory();
        var list = await factory.CreateClient().GetFromJsonAsync<List<JsonElement>>("/_chameleon/sandboxes");
        Assert.Equal(["default"], list!.Select(s => s.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task Delete_persistent_sandbox_named_qa_requires_confirm_only()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "qa", permanent = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await control.DeleteAsync("/_chameleon/sandboxes/qa")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await control.DeleteAsync("/_chameleon/sandboxes/qa?confirm=qa")).StatusCode);
    }

    [Fact]
    public async Task Confirmed_reset_clears_hml_only()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var qa = await AuthorizedClient(factory, "qa");
        var hml = await AuthorizedClient(factory, "hml");
        var txid = NewTxId();
        await CreateCob(qa, txid);
        await CreateCob(hml, txid);
        await Arm(hml, "PUT /bradesco/v2/cob/{txid}");

        var control = factory.CreateClient();
        (await control.PostAsync("/_chameleon/sandboxes/hml/reset?confirm=hml", null)).EnsureSuccessStatusCode();

        hml = await AuthorizedClient(factory, "hml"); // the old token went with the reset
        Assert.Equal(HttpStatusCode.NotFound, (await hml.GetAsync($"bradesco/v2/cob/{txid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateCob(hml, NewTxId())).StatusCode); // fault cleared
        Assert.Equal("ATIVA", await Status(qa, txid));

        var sandbox = await control.GetFromJsonAsync<JsonElement>("/_chameleon/sandboxes/hml");
        Assert.True(sandbox.GetProperty("persistent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sandbox.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task Clock_advance_applies_only_to_its_sandbox()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-a");
        await CreateSandbox(factory, "sb-b");
        var a = await AuthorizedClient(factory, "sb-a");
        var b = await AuthorizedClient(factory, "sb-b");
        var txid = NewTxId();
        await CreateCob(a, txid);
        await CreateCob(b, txid);

        (await a.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 7200 })).EnsureSuccessStatusCode();

        a = await AuthorizedClient(factory, "sb-a"); // its token expired with the advance
        Assert.Equal("REMOVIDA_PELO_PSP", await Status(a, txid));
        Assert.Equal("ATIVA", await Status(b, txid));

        var transition = (await a.GetFromJsonAsync<JsonElement>($"_chameleon/journal?entityId={txid}&kind=StateTransition"))
            .EnumerateArray().Last();
        Assert.Equal("ttl", transition.GetProperty("detail").GetString());
        Assert.True(transition.GetProperty("byClock").GetBoolean());

        // The creation came from a call: same correlation as the PUT, not the clock.
        var created = (await a.GetFromJsonAsync<JsonElement>($"_chameleon/journal?entityId={txid}&kind=StateTransition")).EnumerateArray().First();
        Assert.False(created.GetProperty("byClock").GetBoolean());
        var byClock = (await a.GetFromJsonAsync<JsonElement>("_chameleon/interactions?byClock=true")).GetProperty("items");
        Assert.Equal([txid], byClock.EnumerateArray().Select(i => i.GetProperty("entityId").GetString()));
    }

    [Fact]
    public async Task Freeze_and_resume_realtime()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-rt", clockMode: "Realtime");
        var control = factory.CreateClient();

        var t0 = await Now(control, "sb-rt");
        await ChameleonFactory.EventuallyAsync(async () => await Now(control, "sb-rt") > t0, timeoutMs: 5000);

        (await control.PostAsJsonAsync("/_chameleon/sandboxes/sb-rt/clock", new { mode = "Frozen" })).EnsureSuccessStatusCode();
        var frozen = await Now(control, "sb-rt");
        await Task.Delay(300);
        Assert.Equal(frozen, await Now(control, "sb-rt"));

        (await control.PostAsJsonAsync("/_chameleon/sandboxes/sb-rt/clock", new { mode = "Realtime" })).EnsureSuccessStatusCode();
        await ChameleonFactory.EventuallyAsync(async () => await Now(control, "sb-rt") > frozen, timeoutMs: 5000);
        var resumed = await Now(control, "sb-rt");
        Assert.True(resumed > frozen && resumed < frozen.AddSeconds(5), "resumes from where it stopped");
    }

    [Fact]
    public async Task Verify_does_not_see_other_sandbox()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-a");
        await CreateSandbox(factory, "sb-b");
        var a = await AuthorizedClient(factory, "sb-a");
        var txid = NewTxId();
        await CreateCob(a, txid);

        var inA = await Verify(a, new { entityId = txid });
        var inB = await Verify(factory.CreateClient(), new { entityId = txid, sandboxId = "sb-a" }, "sb-b");
        Assert.True(inA.GetProperty("count").GetInt32() > 0);
        Assert.Equal(0, inB.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Fault_fires_only_in_its_sandbox()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-a");
        await CreateSandbox(factory, "sb-b");
        var a = await AuthorizedClient(factory, "sb-a");
        var b = await AuthorizedClient(factory, "sb-b");
        await Arm(a, "PUT /bradesco/v2/cob/{txid}");

        Assert.Equal(HttpStatusCode.Created, (await CreateCob(b, NewTxId())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateCob(a, NewTxId())).StatusCode);
    }

    [Fact]
    public async Task Token_from_one_sandbox_is_rejected_in_another()
    {
        using var factory = new ChameleonFactory();
        await CreateSandbox(factory, "sb-a");
        await CreateSandbox(factory, "sb-b");
        var a = await AuthorizedClient(factory, "sb-a");

        var b = await AuthorizedClient(factory, "sb-b");
        b.DefaultRequestHeaders.Authorization = a.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateCob(b, NewTxId())).StatusCode);
    }

    [Fact]
    public async Task Sandbox_ids_are_validated_and_unique()
    {
        using var factory = new ChameleonFactory().WithPersistent("qa", "hml");
        var control = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "With Space" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "qa" })).StatusCode);
    }

    [Fact]
    public async Task Copy_takes_entities_faults_policy_and_clock_but_not_history()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        await CreateSandbox(factory, "sb-src");
        var src = await AuthorizedClient(factory, "sb-src");
        var txid = NewTxId();
        await CreateCob(src, txid);
        await Arm(src, "PUT /bradesco/v2/cob/{txid}");
        (await control.PutAsJsonAsync("/_chameleon/sandboxes/sb-src/webhook-policy", new { maxAttempts = 7, baseDelayMs = 10, timeoutMs = 500 }))
            .EnsureSuccessStatusCode();
        (await src.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 600 })).EnsureSuccessStatusCode();

        var created = await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "sb-copy", from = "sb-src", clockMode = "Realtime" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var copy = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Frozen", copy.GetProperty("clockMode").GetString());
        Assert.Equal(await Now(control, "sb-src"), copy.GetProperty("now").GetDateTimeOffset());
        Assert.Equal(7, copy.GetProperty("webhookPolicy").GetProperty("maxAttempts").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, copy.GetProperty("expiresAt").ValueKind);

        var cp = SandboxClient(factory, "sb-copy");
        Assert.Equal(0, (await cp.GetFromJsonAsync<JsonElement>("_chameleon/journal")).GetArrayLength());
        Assert.Single((await cp.GetFromJsonAsync<JsonElement>("_chameleon/faults")).EnumerateArray());
        cp = await AuthorizedClient(factory, "sb-copy");
        Assert.Equal("ATIVA", await Status(cp, txid));

        // Changing the copy leaves the source alone.
        (await cp.PostAsync($"_chameleon/bradesco/cob/{txid}/pay", null)).EnsureSuccessStatusCode();
        Assert.Equal("CONCLUIDA", await Status(cp, txid));
        Assert.Equal("ATIVA", await Status(src, txid));
    }

    [Fact]
    public async Task Copy_from_unknown_sandbox_is_refused()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        var resp = await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "sb-y", from = "sb-x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await control.GetAsync("/_chameleon/sandboxes/sb-y")).StatusCode);
    }

    [Fact]
    public async Task Now_returns_clock_to_real_time_and_keeps_data()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        await CreateSandbox(factory, "sb-now");
        var http = await AuthorizedClient(factory, "sb-now");
        var txid = NewTxId();
        await CreateCob(http, txid);
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 1800 })).EnsureSuccessStatusCode();

        var ahead = await control.GetFromJsonAsync<JsonElement>("/_chameleon/sandboxes/sb-now");
        Assert.InRange(ahead.GetProperty("driftSeconds").GetDouble(), 1790, 1810);

        var resp = await control.PostAsync("/_chameleon/sandboxes/sb-now/clock/now", null);
        var now = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Realtime", now.GetProperty("clockMode").GetString());
        Assert.InRange(now.GetProperty("driftSeconds").GetDouble(), -1, 1);

        var journal = await http.GetFromJsonAsync<JsonElement>($"_chameleon/journal?entityId={txid}");
        Assert.True(journal.GetArrayLength() > 0);
        Assert.Equal("ATIVA", await Status(http, txid));
    }

    [Fact]
    public async Task Clock_moves_back_without_undoing_or_reprocessing()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        await CreateSandbox(factory, "sb-back");
        var http = await AuthorizedClient(factory, "sb-back");
        var expired = NewTxId();
        await CreateCob(http, expired);
        var t0 = await Now(control, "sb-back");

        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 7200 })).EnsureSuccessStatusCode();
        var back = await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = -7200 });
        back.EnsureSuccessStatusCode();
        Assert.Equal(t0, (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("now").GetDateTimeOffset());
        Assert.Equal(t0, await Now(control, "sb-back"));

        http = await AuthorizedClient(factory, "sb-back");
        Assert.Equal("REMOVIDA_PELO_PSP", await Status(http, expired));
        var pending = NewTxId();
        await CreateCob(http, pending);

        // Already past again: the removed charge is not moved a second time; the new one expires once.
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = -3600 })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 4000 })).EnsureSuccessStatusCode();
        http = await AuthorizedClient(factory, "sb-back");
        Assert.Equal("ATIVA", await Status(http, pending));
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 7200 })).EnsureSuccessStatusCode();
        http = await AuthorizedClient(factory, "sb-back");
        Assert.Equal("REMOVIDA_PELO_PSP", await Status(http, pending));
        var transitions = await http.GetFromJsonAsync<JsonElement>($"_chameleon/journal?entityId={expired}&kind=StateTransition");
        Assert.Single(transitions.EnumerateArray(), t => t.GetProperty("byClock").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = -400 * 86400 })).StatusCode);
    }

    [Fact]
    public async Task Created_permanent_sandbox_has_no_ttl_and_keeps_the_protections()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await control.PostAsJsonAsync("/_chameleon/sandboxes",
            new { id = "team", permanent = true, ttlSeconds = 3600 })).StatusCode);

        var created = await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "team", permanent = true, clockMode = "Realtime" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sandbox = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sandbox.GetProperty("persistent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sandbox.GetProperty("expiresAt").ValueKind);

        Assert.Equal(HttpStatusCode.Conflict, (await control.DeleteAsync("/_chameleon/sandboxes/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await control.PostAsync("/_chameleon/sandboxes/team/reset", null)).StatusCode);
        (await control.PostAsync("/_chameleon/sandboxes/team/reset?confirm=team", null)).EnsureSuccessStatusCode();
        Assert.True((await control.GetFromJsonAsync<JsonElement>("/_chameleon/sandboxes/team")).GetProperty("persistent").GetBoolean());

        Assert.Equal(HttpStatusCode.Conflict, (await control.DeleteAsync("/_chameleon/sandboxes/team?confirm=outro")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await control.DeleteAsync("/_chameleon/sandboxes/team?confirm=team")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await control.GetAsync("/_chameleon/sandboxes/team")).StatusCode);
    }

    [Fact] // Status class filter
    public async Task Interactions_filter_by_status_class()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory, "default");
        await CreateCob(http, NewTxId());
        await http.GetAsync($"bradesco/v2/cob/{NewTxId()}"); // 404

        var fours = (await http.GetFromJsonAsync<JsonElement>("_chameleon/interactions?statusClass=4")).GetProperty("items");
        Assert.NotEmpty(fours.EnumerateArray());
        Assert.All(fours.EnumerateArray(), i => Assert.Equal(4, i.GetProperty("statusCode").GetInt32() / 100));
        var twos = (await http.GetFromJsonAsync<JsonElement>("_chameleon/interactions?statusClass=2")).GetProperty("items");
        Assert.All(twos.EnumerateArray(), i => Assert.Equal(2, i.GetProperty("statusCode").GetInt32() / 100));
    }

    private static HttpClient SandboxClient(ChameleonFactory factory, string sandbox)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{sandbox}/");
        return http;
    }

    private static async Task<HttpClient> AuthorizedClient(ChameleonFactory factory, string sandbox)
    {
        var http = SandboxClient(factory, sandbox);
        await TestAuth.Bradesco(http, sandbox);
        return http;
    }

    private static async Task CreateSandbox(ChameleonFactory factory, string id, int? ttlSeconds = null, string? clockMode = null) =>
        (await factory.CreateClient().PostAsJsonAsync("/_chameleon/sandboxes", new { id, ttlSeconds, clockMode }))
            .EnsureSuccessStatusCode();

    private static async Task<string?> Status(HttpClient http, string txid) =>
        (await http.GetFromJsonAsync<JsonElement>($"bradesco/v2/cob/{txid}")).GetProperty("status").GetString();

    private static async Task<DateTimeOffset> Now(HttpClient control, string sandbox) =>
        (await control.GetFromJsonAsync<JsonElement>($"/_chameleon/sandboxes/{sandbox}")).GetProperty("now").GetDateTimeOffset();

    private static async Task<JsonElement> Verify(HttpClient http, object query, string? sandbox = null)
    {
        var path = sandbox is null ? "_chameleon/verify" : $"/s/{sandbox}/_chameleon/verify";
        var resp = await http.PostAsJsonAsync(path, query);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task Arm(HttpClient http, string route) =>
        (await http.PostAsJsonAsync("_chameleon/faults", new { connector = "bradesco", route, kind = "Error", status = 503, times = 1 }))
            .EnsureSuccessStatusCode();
}
