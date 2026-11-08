using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static Chameleon.IntegrationTests.InterTests;

namespace Chameleon.IntegrationTests;

/// Zendesk as a helpdesk integration and its status webhook receiver use it.
public sealed class ZendeskTests
{
    // The synthetic sample account of the connector (ZendeskAccount).
    internal const long Document = 910000000002;
    internal const long OrderId = 910000000006;
    internal const long CaseStatus = 910000000005;
    internal const long Amount = 910000000007;
    // A field the sample account does not declare: stored and searchable like any other.
    internal const long RelatedCase = 910000000099;
    internal const long FormId = 910000000200;

    internal static async Task<(HttpClient Http, Dictionary<string, string> Issued)> Sandbox(ChameleonFactory factory, string id)
    {
        var control = factory.CreateClient();
        (await control.PostAsJsonAsync("/_chameleon/sandboxes", new { id })).EnsureSuccessStatusCode();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/s/{id}/");
        var issued = await TestAuth.Subscribe(http, "zendesk");
        http.DefaultRequestHeaders.Authorization = Basic($"{issued["EMAIL"]}/token", issued["API_TOKEN"]);
        return (http, issued);
    }

    internal static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// A typical client body: snake_case, nulls omitted, values as strings.
    internal static object CreateBody(string externalId, string orderId = "ORD-1001", string document = "12345678000190",
        string caseStatus = "case_status_in_progress", string email = "requester@example.com") => new
    {
        ticket = new
        {
            subject = $"Case {externalId}",
            comment = new { body = "The amount charged differs from the agreed one", @public = true },
            requester = new { name = "Acme Customer", email },
            brand_id = 910000000100,
            ticket_form_id = FormId,
            external_id = externalId,
            custom_fields = new object[]
            {
                new { id = Document, value = document },
                new { id = OrderId, value = orderId },
                new { id = CaseStatus, value = caseStatus },
            },
            tags = new[] { "sample_case" },
        },
    };

    internal static async Task<JsonElement> Create(HttpClient http, string externalId, string orderId = "ORD-1001")
    {
        var resp = await http.PostAsJsonAsync("zendesk/api/v2/tickets", CreateBody(externalId, orderId));
        resp.AssertStatus(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket");
    }

    internal static Task<HttpResponseMessage> Update(HttpClient http, long id, object ticket) =>
        http.PutAsJsonAsync($"zendesk/api/v2/tickets/{id}", new { ticket });

    private static string[] Tags(JsonElement ticket) => ticket.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray();

    [Fact]
    public async Task Create_in_the_client_shape_returns_the_ticket_its_requester_and_first_comment()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-create");

        var ticket = await Create(http, "C-1");
        var id = ticket.GetProperty("id").GetInt64();
        Assert.True(id > 900000);
        Assert.Equal("new", ticket.GetProperty("status").GetString());
        Assert.Equal("C-1", ticket.GetProperty("external_id").GetString());
        Assert.Equal(FormId, ticket.GetProperty("ticket_form_id").GetInt64());
        // The option field mirrors its value as a tag.
        Assert.Equal(["sample_case", "case_status_in_progress"], Tags(ticket));
        Assert.Contains(ticket.GetProperty("custom_fields").EnumerateArray(),
            f => f.GetProperty("id").GetInt64() == OrderId && f.GetProperty("value").GetString() == "ORD-1001");
        Assert.EndsWith($"/s/ci-zendesk-create/zendesk/api/v2/tickets/{id}.json", ticket.GetProperty("url").GetString());

        var fetched = (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}")).GetProperty("ticket");
        Assert.Equal(id, fetched.GetProperty("id").GetInt64());

        var requester = ticket.GetProperty("requester_id").GetInt64();
        var user = (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/users/{requester}")).GetProperty("user");
        Assert.Equal("requester@example.com", user.GetProperty("email").GetString());
        // Same e-mail, same user.
        Assert.Equal(requester, (await Create(http, "C-2")).GetProperty("requester_id").GetInt64());

        var comments = await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}/comments");
        var first = Assert.Single(comments.GetProperty("comments").EnumerateArray());
        Assert.Equal("The amount charged differs from the agreed one", first.GetProperty("body").GetString());
        Assert.True(first.GetProperty("public").GetBoolean());
    }

    [Fact]
    public async Task Update_swaps_the_option_tag_moves_status_and_a_closed_ticket_refuses_changes()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-update");
        var id = (await Create(http, "C-10")).GetProperty("id").GetInt64();

        var resp = await Update(http, id, new
        {
            status = "solved",
            comment = new { body = "Case accepted", @public = true },
            custom_fields = new[] { new { id = CaseStatus, value = "case_status_accepted" } },
        });
        resp.AssertStatus(HttpStatusCode.OK);
        var ticket = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket");
        Assert.Equal("solved", ticket.GetProperty("status").GetString());
        Assert.Equal(["sample_case", "case_status_accepted"], Tags(ticket));

        // The comment just written is the newest one (a client reads it right after the PUT).
        var last = await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}/comments?sort_order=desc&per_page=1");
        Assert.Equal("Case accepted", last.GetProperty("comments")[0].GetProperty("body").GetString());
        Assert.Equal(2, last.GetProperty("count").GetInt32());

        (await Update(http, id, new { status = "new" })).AssertStatus((HttpStatusCode)422);
        (await Update(http, id, new { status = "closed" })).AssertStatus(HttpStatusCode.OK);
        var closed = await Update(http, id, new { tags = new[] { "x" } });
        closed.AssertStatus((HttpStatusCode)422);
        Assert.Contains("closed prevents ticket update", await closed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Errors_have_the_provider_shape()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-zendesk-errors");

        var missing = await http.GetAsync("zendesk/api/v2/tickets/1");
        missing.AssertStatus(HttpStatusCode.NotFound);
        Assert.Equal("RecordNotFound", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var blank = await http.PostAsJsonAsync("zendesk/api/v2/tickets", new { ticket = new { subject = "no comment" } });
        blank.AssertStatus((HttpStatusCode)422);
        Assert.Equal("RecordInvalid", (await blank.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        http.DefaultRequestHeaders.Authorization = Basic($"{issued["EMAIL"]}/token", "wrong");
        var refused = await http.GetAsync("zendesk/api/v2/tickets/1");
        refused.AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Equal("Couldn't authenticate you", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The e-mail without "/token" is a password login, not the API token.
        http.DefaultRequestHeaders.Authorization = Basic(issued["EMAIL"], issued["API_TOKEN"]);
        (await http.GetAsync("zendesk/api/v2/tickets/1")).AssertStatus(HttpStatusCode.Unauthorized);
    }

    internal static async Task<string> Upload(HttpClient http, string fileName, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/binary");
        var resp = await http.PostAsync($"zendesk/api/v2/uploads?filename={fileName}", content);
        resp.AssertStatus(HttpStatusCode.Created);
        var upload = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("upload");
        Assert.Equal(bytes.Length, upload.GetProperty("attachment").GetProperty("size").GetInt64());
        return upload.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Upload_token_attaches_once_and_content_url_serves_the_bytes()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-upload");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 receipt");
        var token = await Upload(http, "receipt.pdf", bytes);
        var id = (await Create(http, "C-30")).GetProperty("id").GetInt64();

        (await Update(http, id, new { comment = new { body = "Receipt attached", @public = true, uploads = new[] { token } } })).AssertStatus(HttpStatusCode.OK);
        var comment = (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}/comments?sort_order=desc&per_page=1")).GetProperty("comments")[0];
        var attachment = Assert.Single(comment.GetProperty("attachments").EnumerateArray());
        Assert.Equal("receipt.pdf", attachment.GetProperty("file_name").GetString());
        var served = await http.GetByteArrayAsync(new Uri(attachment.GetProperty("content_url").GetString()!).PathAndQuery);
        Assert.Equal(bytes, served);

        // A token is consumed by the comment that used it.
        (await Update(http, id, new { comment = new { body = "Again", uploads = new[] { token } } })).AssertStatus((HttpStatusCode)422);
        (await http.PostAsync("zendesk/api/v2/uploads", new ByteArrayContent(bytes))).AssertStatus((HttpStatusCode)422);

        // The binary body goes on record by size only.
        var calls = await http.PostAsJsonAsync("_chameleon/verify", new { connector = "zendesk", summaryContains = "uploads" });
        Assert.Contains($"<binary {bytes.Length} bytes>", await calls.Content.ReadAsStringAsync());
    }

    internal static async Task<JsonElement> Search(HttpClient http, string query, string extra = "")
    {
        var resp = await http.GetAsync($"zendesk/api/v2/search?query={Uri.EscapeDataString(query)}{extra}");
        resp.AssertStatus(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] ExternalIds(JsonElement search) =>
        search.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("external_id").GetString()!).ToArray();

    [Fact]
    public async Task Search_answers_the_terms_a_client_builds()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-search");
        await Create(http, "C-40", orderId: "ORD-40");
        await Create(http, "C-41", orderId: "ORD-41");
        var related = (await Create(http, "C-42", orderId: "ORD-41")).GetProperty("id").GetInt64();
        (await Update(http, related, new { custom_fields = new[] { new { id = RelatedCase, value = "C-41" } } })).AssertStatus(HttpStatusCode.OK);
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd");

        var byOrder = await Search(http, $"type:ticket custom_field_{OrderId}:ORD-41", "&sort_by=created_at&sort_order=asc");
        Assert.Equal(["C-41", "C-42"], ExternalIds(byOrder));
        Assert.Equal("ticket", byOrder.GetProperty("results")[0].GetProperty("result_type").GetString());
        Assert.Equal(["C-42", "C-41"], ExternalIds(await Search(http, $"type:ticket custom_field_{OrderId}:ORD-41", "&sort_by=created_at&sort_order=desc")));
        Assert.Equal(["C-40"], ExternalIds(await Search(http, "type:ticket external_id:C-40")));
        Assert.Equal(["C-42"], ExternalIds(await Search(http, $"type:ticket custom_field_{RelatedCase}:*")));
        Assert.Equal(3, (await Search(http, $"type:ticket status:new created>={today} created<={today} tags:\"sample_case\"")).GetProperty("count").GetInt32());
        Assert.Equal(0, (await Search(http, "type:ticket created>=2999-01-01")).GetProperty("count").GetInt32());
        // Unquoted value with a space: the field only gets the first word.
        Assert.Equal(0, (await Search(http, $"type:ticket custom_field_{Document}:12345678000190 extra")).GetProperty("count").GetInt32());

        var paged = await Search(http, "type:ticket", "&per_page=2&page=1");
        Assert.Equal(2, paged.GetProperty("results").GetArrayLength());
        Assert.Contains("page=2", paged.GetProperty("next_page").GetString());

        (await http.GetAsync("zendesk/api/v2/search?query=type:ticket&per_page=100&page=11")).AssertStatus((HttpStatusCode)422);
        (await http.GetAsync("zendesk/api/v2/search?query=type:ticket&per_page=101")).AssertStatus((HttpStatusCode)422);
    }

    [Fact]
    public async Task Search_index_delay_hides_a_new_ticket_until_the_clock_passes_it()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-index");
        (await http.PutAsJsonAsync("_chameleon/connectors/zendesk/subscription/settings", new Dictionary<string, string> { ["SEARCH_INDEX_DELAY_SECONDS"] = "60" })).EnsureSuccessStatusCode();
        await Create(http, "C-50");

        Assert.Equal(0, (await Search(http, "type:ticket external_id:C-50")).GetProperty("count").GetInt32());
        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 61 })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await Search(http, "type:ticket external_id:C-50")).GetProperty("count").GetInt32());
    }

    internal const string Hook = "https://app-under-test.local/webhooks/zendesk";

    /// The receiver check: base64(HMAC-SHA256(secret, timestamp + raw body)).
    internal static bool SignatureValid(string secret, Dictionary<string, string> headers, string body) =>
        headers.TryGetValue("x-zendesk-webhook-signature", out var signature)
        && signature == Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(headers["x-zendesk-webhook-signature-timestamp"] + body)));

    private static Task<HttpResponseMessage> Action(HttpClient http, long id, string action, object? args = null) =>
        http.PostAsJsonAsync($"_chameleon/connectors/zendesk/entities/ticket/{id}/actions/{action}", args ?? new { });

    [Fact]
    public async Task Status_change_sends_the_signed_event_a_receiver_verifies()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-zendesk-event");
        (await http.PutAsJsonAsync("_chameleon/connectors/zendesk/webhook-registrations/status_changed", new { url = Hook })).EnsureSuccessStatusCode();
        var id = (await Create(http, "C-60")).GetProperty("id").GetInt64();
        await factory.SettledAsync("ci-zendesk-event");
        Assert.Empty(factory.Webhooks.Received); // creating is not a status change

        (await Update(http, id, new { status = "open" })).AssertStatus(HttpStatusCode.OK);
        await factory.SettledAsync("ci-zendesk-event");

        var (url, body, _) = Assert.Single(factory.Webhooks.Received);
        Assert.Equal(Hook, url);
        Assert.True(SignatureValid(issued["WEBHOOK_SECRET"], factory.Webhooks.ReceivedHeaders.Single(), body));
        var evt = JsonDocument.Parse(body).RootElement;
        Assert.Equal("zen:event-type:ticket.status_changed", evt.GetProperty("type").GetString());
        var detail = evt.GetProperty("detail");
        Assert.Equal((id.ToString(), "C-60", FormId.ToString()),
            (detail.GetProperty("id").GetString(), detail.GetProperty("external_id").GetString(), detail.GetProperty("form_id").GetString()));
        Assert.Equal(("OPEN", "NEW"), (evt.GetProperty("event").GetProperty("current").GetString(), evt.GetProperty("event").GetProperty("previous").GetString()));
        Assert.Contains("sample_case", detail.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Action_resolves_with_an_outcome_and_the_clock_closes_after_four_days()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-zendesk-resolve");
        (await http.PutAsJsonAsync("_chameleon/connectors/zendesk/webhook-registrations/status_changed", new { url = Hook })).EnsureSuccessStatusCode();
        var id = (await Create(http, "C-70")).GetProperty("id").GetInt64();

        (await Action(http, id, "forward_to_third_party", new { comment = "Forwarded to the supplier" })).AssertStatus(HttpStatusCode.OK);
        (await Action(http, id, "comment", new { text = "Internal note", @public = false })).AssertStatus(HttpStatusCode.OK);
        (await Action(http, id, "resolve", new { outcome = "maybe" })).AssertStatus(HttpStatusCode.BadRequest);
        (await Action(http, id, "resolve", new { outcome = "partially_accepted", amount = 9.1, comment = "Partially accepted" })).AssertStatus(HttpStatusCode.OK);
        (await Action(http, id, "mark_refunded")).AssertStatus(HttpStatusCode.OK);

        var ticket = (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}")).GetProperty("ticket");
        Assert.Equal("solved", ticket.GetProperty("status").GetString());
        Assert.Equal(["sample_case", "case_status_partially_accepted", "refund_done"], Tags(ticket));
        Assert.Contains(ticket.GetProperty("custom_fields").EnumerateArray(),
            f => f.GetProperty("id").GetInt64() == Amount && f.GetProperty("value").GetString() == "9.10");
        var comments = (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}/comments")).GetProperty("comments");
        Assert.Equal(4, comments.GetArrayLength());
        Assert.False(comments[2].GetProperty("public").GetBoolean());

        (await http.PostAsJsonAsync("_chameleon/clock/advance", new { seconds = 4 * 86400 })).EnsureSuccessStatusCode();
        Assert.Equal("closed", (await http.GetFromJsonAsync<JsonElement>($"zendesk/api/v2/tickets/{id}")).GetProperty("ticket").GetProperty("status").GetString());
        (await Update(http, id, new { status = "open" })).AssertStatus((HttpStatusCode)422);
        (await Action(http, id, "comment", new { text = "too late" })).AssertStatus(HttpStatusCode.Conflict);

        await factory.SettledAsync("ci-zendesk-resolve");
        var events = factory.Webhooks.Received.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("event").GetProperty("current").GetString()).ToList();
        Assert.Equal(["HOLD", "SOLVED", "CLOSED"], events);
        var solved = JsonDocument.Parse(factory.Webhooks.Received.ElementAt(1).Body).RootElement.GetProperty("detail");
        Assert.Contains("case_status_partially_accepted", solved.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.All(factory.Webhooks.Received.Zip(factory.Webhooks.ReceivedHeaders),
            r => Assert.True(SignatureValid(issued["WEBHOOK_SECRET"], r.Second, r.First.Body)));
    }

    [Fact]
    public async Task Invalid_signature_action_resends_the_last_event_signed_wrong()
    {
        using var factory = new ChameleonFactory();
        var (http, issued) = await Sandbox(factory, "ci-zendesk-badsig");
        (await http.PutAsJsonAsync("_chameleon/connectors/zendesk/webhook-registrations/status_changed", new { url = Hook })).EnsureSuccessStatusCode();
        var id = (await Create(http, "C-80")).GetProperty("id").GetInt64();
        (await Action(http, id, "resend_invalid_signature")).AssertStatus(HttpStatusCode.Conflict);
        (await Action(http, id, "start_analysis")).AssertStatus(HttpStatusCode.OK);
        (await Action(http, id, "resend_invalid_signature")).AssertStatus(HttpStatusCode.OK);
        await factory.SettledAsync("ci-zendesk-badsig");

        var received = factory.Webhooks.Received.Zip(factory.Webhooks.ReceivedHeaders).ToList();
        Assert.Equal(2, received.Count);
        Assert.Equal(received[0].First.Body, received[1].First.Body);
        Assert.True(SignatureValid(issued["WEBHOOK_SECRET"], received[0].Second, received[0].First.Body));
        Assert.False(SignatureValid(issued["WEBHOOK_SECRET"], received[1].Second, received[1].First.Body));
    }

    [Fact]
    public async Task History_keeps_document_name_and_phone_masked()
    {
        using var factory = new ChameleonFactory();
        var (http, _) = await Sandbox(factory, "ci-zendesk-lgpd");
        var id = (await Create(http, "C-20")).GetProperty("id").GetInt64();

        var calls = await http.PostAsJsonAsync("_chameleon/verify", new { connector = "zendesk", entityId = id.ToString(), kind = "InboundRequest" });
        var text = await calls.Content.ReadAsStringAsync();
        Assert.Contains("custom_fields", text);
        Assert.DoesNotContain("12345678000190", text);
        Assert.DoesNotContain("Acme Customer", text);
        Assert.DoesNotContain("requester@example.com", text);
    }
}
