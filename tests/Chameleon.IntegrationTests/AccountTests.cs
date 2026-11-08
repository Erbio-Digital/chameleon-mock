using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Chameleon.Engine;
using static Chameleon.IntegrationTests.BradescoPixTests;

namespace Chameleon.IntegrationTests;

/// Bank accounts, balance and ledger; Hold on webhook delivery.
public sealed class AccountTests
{
    [Fact]
    public async Task Operator_opens_account_and_sets_balance_by_adjustment()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();

        var opened = await Open(http, "operacao", "1234", "56789-0", "pix-account@example.com");
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var account = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("1234", "56789-0", "pix-account@example.com", "0.00"),
            (Str(account, "branch"), Str(account, "account"), Str(account, "pixKey"), Str(account, "balance")));

        (await http.PutAsJsonAsync("/_chameleon/connectors/bradesco/accounts/operacao/balance", new { balance = 5000m })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/_chameleon/connectors/bradesco/accounts/operacao/balance", new { balance = 4200.5m })).EnsureSuccessStatusCode();

        var detail = await http.GetFromJsonAsync<JsonElement>("/_chameleon/accounts/operacao");
        Assert.Equal("4200.50", Str(detail, "balance"));
        var entries = detail.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["-799.50", "5000.00"], entries.Select(e => Str(e, "amount")));
        Assert.All(entries, e => Assert.Equal("Adjustment", Str(e, "kind")));

        // Opening and setting are control actions on record; reads are not.
        var actions = await Verify(http, new { connector = "bradesco", entityId = "operacao", kind = "ControlAction" });
        Assert.Equal(3, actions.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Paid_charge_credits_only_the_account_that_owns_the_key()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        await Open(http, "matriz", "0001", "111-1", "chave-matriz");
        await Open(http, "filial", "0001", "222-2", "chave-filial");
        await Open(http, "sem-chave", "0001", "333-3");

        await PayCob(http, "chave-matriz", "100.00");
        await PayCob(http, "chave-filial", "40.00");
        await PayCob(http, "chave-de-ninguem", "7.00"); // no owner: paid, no credit

        var matriz = await http.GetFromJsonAsync<JsonElement>("/_chameleon/accounts/matriz");
        Assert.Equal("100.00", Str(matriz, "balance"));
        var credit = Assert.Single(matriz.GetProperty("entries").EnumerateArray());
        Assert.Equal(("Credit", "Pix recebido"), (Str(credit, "kind"), Str(credit, "description")));
        Assert.StartsWith("E", Str(credit, "reference"));

        var all = await http.GetFromJsonAsync<JsonElement>("/_chameleon/accounts?bank=bradesco");
        var balances = all.EnumerateArray().ToDictionary(a => Str(a, "nickname")!, a => Str(a, "balance"));
        Assert.Equal(("100.00", "40.00", "0.00"), (balances["matriz"], balances["filial"], balances["sem-chave"]));
    }

    [Fact]
    public async Task Repeated_nickname_branch_and_account_or_pix_key_is_refused()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        await Open(http, "principal", "1234", "56789-0", "pix-account@example.com");

        var sameAccount = await Open(http, "outra", "1234", "56789-0");
        Assert.Equal(HttpStatusCode.Conflict, sameAccount.StatusCode);
        Assert.Contains("branch 1234 and account 56789-0", await sameAccount.Content.ReadAsStringAsync());

        var sameKey = await Open(http, "outra", "9999", "1", "pix-account@example.com");
        Assert.Contains("already belongs to account principal", await sameKey.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Open(http, "principal", "9999", "2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Open(http, "With Space", "1234", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Open(http, "x", "12", "1")).StatusCode);

        Assert.Single((await http.GetFromJsonAsync<JsonElement>("/_chameleon/accounts")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/_chameleon/accounts/inexistente")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/_chameleon/connectors/nfse-sp/accounts", new { nickname = "y", branch = "1234", account = "1" })).StatusCode);
    }

    [Fact]
    public async Task Operator_edits_branch_account_and_key_keeping_balance_and_ledger()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        await Open(http, "operacao", "1234", "56789-0", "old-key", 300m);
        await Open(http, "outra", "4321", "11111-1", "other-key");

        var edited = await Edit(http, "operacao", new { branch = "0001", account = "70001-0", pixKey = "new-key" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var account = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("0001", "70001-0", "new-key", "300.00"),
            (Str(account, "branch"), Str(account, "account"), Str(account, "pixKey"), Str(account, "balance")));
        Assert.Single(account.GetProperty("entries").EnumerateArray());

        // Absent field stays; empty key removes it.
        var keyless = await (await Edit(http, "operacao", new { pixKey = "" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("0001", "70001-0"), (Str(keyless, "branch"), Str(keyless, "account")));
        Assert.Equal(JsonValueKind.Null, keyless.GetProperty("pixKey").ValueKind);

        // Same rules as opening, against the other accounts only (saving itself unchanged is fine).
        Assert.Equal(HttpStatusCode.OK, (await Edit(http, "operacao", new { conta = "70001-0" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Edit(http, "operacao", new { pixKey = "other-key" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Edit(http, "operacao", new { branch = "4321", account = "11111-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(http, "operacao", new { branch = "12" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Edit(http, "ninguem", new { conta = "1" })).StatusCode);

        var actions = await Verify(http, new { connector = "bradesco", entityId = "operacao", kind = "ControlAction" });
        Assert.True(actions.GetProperty("count").GetInt32() >= 3);
    }

    [Fact] // the balance lookup goes without the check digit: 12345-1 and 12345-6 would be the same account to it
    public async Task Account_that_differs_from_another_only_in_the_check_digit_is_refused()
    {
        using var factory = new ChameleonFactory();
        var http = factory.CreateClient();
        await Open(http, "acme", "1234", "12345-1");

        var twin = await Open(http, "parceiro", "1234", "12345-6");
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Contains("only the check digit differs", await twin.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Open(http, "parceiro", "1234", "12345")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Open(http, "outra-agencia", "4321", "12345-6")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Open(http, "parceiro", "1234", "67890-3")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Edit(http, "parceiro", new { account = "12345-6" })).StatusCode);
    }

    [Fact]
    public async Task Accounts_do_not_leak_between_sandboxes()
    {
        using var factory = new ChameleonFactory();
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id = "ci-accounts" })).EnsureSuccessStatusCode();
        var ci = factory.CreateClient();
        ci.BaseAddress = new Uri(ci.BaseAddress!, "/s/ci-accounts/");

        await Open(control, "principal", "1234", "1", balance: 5000m);
        Assert.Equal(HttpStatusCode.Created, (await ci.PostAsJsonAsync("_chameleon/connectors/bradesco/accounts",
            new { nickname = "principal", branch = "1234", account = "1" })).StatusCode);

        Assert.Equal("0.00", Str(await ci.GetFromJsonAsync<JsonElement>("_chameleon/accounts/principal"), "balance"));
        Assert.Equal("5000.00", Str(await control.GetFromJsonAsync<JsonElement>("/_chameleon/accounts/principal"), "balance"));
    }

    [Fact]
    public async Task Held_delivery_waits_without_attempt_until_disarmed()
    {
        using var factory = new ChameleonFactory();
        var http = await AuthorizedClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "webhook", kind = "Hold", times = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/_chameleon/faults",
            new { connector = "bradesco", route = "PUT /bradesco/v2/cob/{txid}", kind = "Hold" })).StatusCode);
        (await http.PostAsJsonAsync("/_chameleon/faults", new { connector = "bradesco", route = "webhook", kind = "Hold" })).EnsureSuccessStatusCode();

        var txid = NewTxId();
        await CreateCob(http, txid);
        var paid = await http.PostAsync($"/_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null);
        Assert.Equal("CONCLUIDA", Str(await paid.Content.ReadFromJsonAsync<JsonElement>(), "status"));

        await Task.Delay(1500);
        var outbox = factory.Services.GetRequiredService<IWebhookOutbox>();
        var held = Assert.Single(outbox.Find(Sandbox.Default, txid));
        Assert.Equal((DeliveryStatus.Pending, 0), (held.Status, held.Attempts));
        Assert.Empty(factory.Webhooks.Received);
        Assert.Contains((await http.GetFromJsonAsync<JsonElement>("/_chameleon/faults")).EnumerateArray(), f => Str(f, "kind") == "Hold");

        (await http.DeleteAsync("/_chameleon/faults?connector=bradesco&route=webhook")).EnsureSuccessStatusCode();
        await factory.SettledAsync();
        Assert.Equal((DeliveryStatus.Delivered, 1), (outbox.Find(Sandbox.Default, txid)[0].Status, outbox.Find(Sandbox.Default, txid)[0].Attempts));
        Assert.Single(factory.Webhooks.Received);
    }

    internal static Task<HttpResponseMessage> Open(HttpClient http, string nickname, string branch, string account,
        string? pixKey = null, decimal? balance = null) =>
        http.PostAsJsonAsync(Path(http, "_chameleon/connectors/bradesco/accounts"), new { nickname, branch, account, pixKey, balance });

    private static Task<HttpResponseMessage> Edit(HttpClient http, string nickname, object body) =>
        http.PatchAsJsonAsync(Path(http, $"_chameleon/connectors/bradesco/accounts/{nickname}"), body);

    private static async Task PayCob(HttpClient http, string chave, string valor)
    {
        var txid = NewTxId();
        (await http.PutAsJsonAsync($"bradesco/v2/cob/{txid}",
            new { calendario = new { expiracao = 3600 }, valor = new { original = valor }, chave })).EnsureSuccessStatusCode();
        (await http.PostAsync($"_chameleon/connectors/bradesco/entities/cob/{txid}/actions/pay", null)).EnsureSuccessStatusCode();
    }

    // Default-sandbox clients have no sandbox prefix; keep the path absolute there.
    private static string Path(HttpClient http, string relative) =>
        http.BaseAddress?.AbsolutePath.StartsWith("/s/") == true ? relative : "/" + relative;

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();
}
