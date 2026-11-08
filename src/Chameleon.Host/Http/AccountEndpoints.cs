using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Host.Http;

/// Bank accounts of the sandbox. Reads are sandbox-wide and stay outside the connector
/// group (not journaled); opening an account and setting a balance run inside the bank's group
/// as control actions, so they respect its subscription. Editing changes branch, account or key, never the nickname.
public static class AccountEndpoints
{
    public sealed record SetBalanceRequest(decimal Balance);

    public static IEndpointRouteBuilder MapAccounts(this IEndpointRouteBuilder routes)
    {
        var prefix = ChameleonEndpoints.ControlPrefix;

        routes.MapGet($"{prefix}/accounts", (RequestScope store, string? bank) =>
            Results.Ok(Banking.Accounts(new ConnectorStore(store)).Where(a => bank is null || a.Bank == bank)
                .OrderBy(a => a.Bank).ThenBy(a => a.Nickname).Select(Summary)));

        routes.MapGet($"{prefix}/accounts/{{nickname}}", (string nickname, RequestScope store) =>
            store.Get<BankAccount>(nickname) is { } a ? Results.Ok(Detail(a)) : NotFound(nickname));
        return routes;
    }

    public static void MapAccountChanges(this RouteGroupBuilder group, IConnector connector)
    {
        var path = $"{ChameleonEndpoints.ControlPrefix}/connectors/{connector.Name}/accounts";

        group.MapPost(path, (OpenAccountRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: req.Nickname);
            var (account, error, conflict) = Banking.Open(ctx, req);
            if (account is null)
                return conflict ? Results.Conflict(new { message = error }) : Results.BadRequest(new { message = error });
            return Results.Json(Detail(account), statusCode: 201);
        });

        group.MapPatch($"{path}/{{nickname}}", (string nickname, UpdateAccountRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: nickname);
            if (ctx.Store.Get<BankAccount>(nickname) is not { } account || account.Bank != connector.Name) return NotFound(nickname);
            var (error, conflict) = Banking.Update(ctx, account, req);
            if (error is not null)
                return conflict ? Results.Conflict(new { message = error }) : Results.BadRequest(new { message = error });
            return Results.Ok(Detail(account));
        });

        group.MapPut($"{path}/{{nickname}}/balance", (string nickname, SetBalanceRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            http.Annotate(entityId: nickname);
            if (ctx.Store.Get<BankAccount>(nickname) is not { } account || account.Bank != connector.Name) return NotFound(nickname);
            if (req.Balance < 0) return Results.BadRequest(new { message = "balance cannot be negative" });
            Banking.SetBalance(ctx, account, req.Balance);
            return Results.Ok(Detail(account));
        });
    }

    private static IResult NotFound(string nickname) => Results.NotFound(new { message = $"account not found: {nickname}" });

    private static object Summary(BankAccount a) => new
    {
        a.Nickname, a.Bank, a.Branch, a.Account, a.PixKey, balance = Banking.Money(a.Balance), a.CreatedAt,
    };

    private static object Detail(BankAccount a) => new
    {
        a.Nickname, a.Bank, a.Branch, a.Account, a.PixKey, balance = Banking.Money(a.Balance), a.CreatedAt,
        entries = a.Entries.AsEnumerable().Reverse().Select(e => new
        {
            e.Id, e.At, kind = e.Kind.ToString(), amount = Banking.Money(e.Amount), e.Description, e.Reference,
        }),
    };
}
