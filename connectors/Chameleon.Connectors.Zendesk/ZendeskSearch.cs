using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Chameleon.Sdk;

namespace Chameleon.Connectors.Zendesk;

/// GET /search for tickets: the terms a client builds, joined by AND, several values of
/// the same property by OR (as Zendesk does), at most 1000 results reachable and an optional index delay.
public sealed partial class ZendeskConnector
{
    private const int SearchLimit = 1000;
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    private sealed record Term(string Key, string Op, string Value);

    private static IResult Search(HttpContext http, ConnectorContext ctx)
    {
        var q = http.Request.Query;
        var (page, perPage) = (Int(q["page"], 1), Int(q["per_page"], 100));
        if (perPage > 100) return SearchInvalid("per_page must be less than or equal to 100");
        if (page * perPage > SearchLimit) return SearchInvalid("Requested response size was greater than Search Response Limits");

        var groups = Terms(q["query"].ToString()).GroupBy(t => t.Key == "" ? Guid.NewGuid().ToString() : t.Key).ToList();
        var found = ctx.Store.All<ZendeskTicket>()
            .Where(t => t.IndexedAt <= ctx.Now && groups.All(g => g.Any(term => Matches(t, term))));
        var desc = q["sort_order"] != "asc";
        found = q["sort_by"].ToString() switch
        {
            "updated_at" => desc ? found.OrderByDescending(t => t.UpdatedAt) : found.OrderBy(t => t.UpdatedAt),
            "status" => desc ? found.OrderByDescending(t => t.Status) : found.OrderBy(t => t.Status),
            _ => desc ? found.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id) : found.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
        };
        var all = found.ToList();
        var results = all.Skip((page - 1) * perPage).Take(perPage).Select(t =>
        {
            var node = JsonSerializer.SerializeToNode(View(http, t), Json)!.AsObject();
            node["result_type"] = "ticket";
            return node;
        }).ToList();

        string PageUrl(int p) => $"{Base(http)}/api/v2/search.json" + QueryString.Create(
            q.Where(kv => kv.Key != "page").Select(kv => KeyValuePair.Create(kv.Key, (string?)kv.Value.ToString()))
                .Append(KeyValuePair.Create("page", (string?)p.ToString())));
        return Results.Json(new
        {
            results,
            facets = (object?)null,
            next_page = page * perPage < Math.Min(all.Count, SearchLimit) ? PageUrl(page + 1) : null,
            previous_page = page > 1 ? PageUrl(page - 1) : null,
            count = all.Count,
        }, Json);
    }

    private static IResult SearchInvalid(string description) =>
        Results.Json(new { error = "invalid", description = $"Invalid search: {description}" }, Json, statusCode: 422);

    /// "key:value", "key>=value", "key:\"a b\"" or a free word. A value with a space and no quotes
    /// ends at the space; the rest becomes free words, as Zendesk does with unquoted values.
    private static IEnumerable<Term> Terms(string query) =>
        TermPattern().Matches(query).Select(m => m.Groups["free"].Success
            ? new Term("", ":", m.Groups["free"].Value)
            : new Term(m.Groups["key"].Value.ToLowerInvariant(), m.Groups["op"].Value, m.Groups["value"].Value.Trim('"')));

    [GeneratedRegex("""(?<key>[A-Za-z_0-9]+)(?<op>>=|<=|:|>|<)(?<value>"[^"]*"|\S+)|(?<free>\S+)""")]
    private static partial Regex TermPattern();

    private static bool Matches(ZendeskTicket t, Term term)
    {
        if (term.Key == "") return Contains(t.Subject, term.Value) || Contains(t.Description, term.Value)
                                   || t.Tags.Any(tag => Contains(tag, term.Value)) || t.CustomFields.Any(f => Contains(f.Value, term.Value));
        if (term.Key.StartsWith("custom_field_") && long.TryParse(term.Key["custom_field_".Length..], out var field))
            return term.Value == "*" ? t.Field(field) is { Length: > 0 } : string.Equals(t.Field(field), term.Value, StringComparison.OrdinalIgnoreCase);
        return term.Key switch
        {
            "type" => term.Value == "ticket",
            "external_id" => string.Equals(t.ExternalId, term.Value, StringComparison.OrdinalIgnoreCase),
            "status" => Enum.GetValues<TicketStatus>().Cast<TicketStatus?>().FirstOrDefault(s => s.ToString() == term.Value.ToLowerInvariant()) is { } s
                        && Compare(t.Status.CompareTo(s), term.Op),
            "created" => Date(term.Value) is { } d && Compare(t.CreatedAt.ToOffset(Brasilia).Date.CompareTo(d), term.Op),
            "updated" => Date(term.Value) is { } d && Compare(t.UpdatedAt.ToOffset(Brasilia).Date.CompareTo(d), term.Op),
            "tags" => term.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t.Tags.Contains),
            "subject" => Contains(t.Subject, term.Value),
            "description" => Contains(t.Description, term.Value),
            _ => false,
        };
    }

    private static bool Compare(int comparison, string op) => op switch
    {
        ">=" => comparison >= 0,
        "<=" => comparison <= 0,
        ">" => comparison > 0,
        "<" => comparison < 0,
        _ => comparison == 0,
    };

    private static DateTime? Date(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}
