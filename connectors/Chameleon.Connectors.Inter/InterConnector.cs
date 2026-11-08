using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Inter;

/// Banco Inter: one integration (one subscription, one certificate) with OAuth v2 scopes, Pix cob
/// (Bacen contract plus the Inter extensions) and Cobranca v3 (boleto with Pix).
public sealed class InterConnector : IConnector
{
    public const string ConnectorName = "inter";
    public const string AccountHeader = "x-conta-corrente";

    internal static readonly PixCobApi<InterPixCob, InterPixWebhook, InterPixDevolucao> Pix =
        new(new PixOptions("/inter/pix/v2") {
            RemoveOnExpiry = false, ExtendedCobApi = true, RequireHttpsWebhook = true, PayerInNotification = true,
            CobV = true, CobVRead = "cobv.read", CobVWrite = "cobv.write", Devolucao = true, PixLookup = true, Ispb = "00416968",
            Locations = (ctx, http, id) => id is null ? NewLoc(ctx, http, "cobv") : ctx.Store.Get<InterLoc>(id.Value.ToString())?.ToPix(),
        });

    /// Scopes the Inter knows; a token gets the requested ones among these.
    private static readonly HashSet<string> KnownScopes =
    [
        "cob.read", "cob.write", "cobv.read", "cobv.write", "payloadlocation.read", "payloadlocation.write",
        "pix.read", "pix.write", "webhook.read", "webhook.write",
        "boleto-cobranca.read", "boleto-cobranca.write",
        "extrato.read", "pagamento-pix.write", "pagamento-pix.read", "pagamento-boleto.write", "pagamento-boleto.read",
        "webhook-banking.write", "webhook-banking.read",
    ];

    /// Account-level webhooks, registered by name next to the Pix keys.
    private static readonly string[] AccountWebhooks = [InterCobrancaApi.WebhookKey, InterBankingApi.PixWebhook, InterBankingApi.BoletoWebhook];

    // ponytail: the Inter retries at 20, 30, 60 and 120 min; the engine only doubles, so 20 min doubling, 5 attempts.
    private static readonly WebhookPolicy Policy = new(MaxAttempts: 5, BaseDelayMs: 1_200_000, TimeoutMs: 10000);

    private static readonly WebhookRegistrations Registrations = new("Pix key or cobranca",
        store => Pix.ListWebhooks(store).Concat(store.All<InterAccountWebhook>()
            .Where(w => w.WebhookUrl is not null).Select(w => new WebhookRegistration(w.Chave, w.WebhookUrl!, w.Criacao))),
        (ctx, key, url) => { if (AccountWebhooks.Contains(key)) SetAccountWebhook(ctx, key, url); else Pix.SetWebhook(ctx, key, url); },
        (ctx, key) => AccountWebhooks.Contains(key) ? UnsetAccountWebhook(ctx, key) : Pix.UnsetWebhook(ctx, key));

    public string Name => ConnectorName;

    public ConnectorDescriptor Describe() => new(Name,
        [Pix.Describe(), Pix.DescribeRefund(), InterCobrancaApi.Cobranca.Describe("codigoSolicitacao"), InterBankingApi.Pagamento.Describe("codigo")],
        [Pix.Pay, Pix.Remove, .. Pix.RefundActions, .. InterCobrancaApi.Actions, .. InterBankingApi.Actions], Policy, Registrations)
    {
        Title = "Inter",
        Category = "banking",
        Description = "Pix cob, cobv and refund, Cobranca v3 (boleto with Pix) and Banking (balance, statement, outgoing Pix, boleto payment), with scoped OAuth and mTLS.",
        Credentials = SimulatedOAuth.Credentials("/oauth/v2/token"),
        TestEvents = TestEvents,
        HasAccounts = true,
        ClientCertificate = true,
        WebhookCertificate = true,
        Settlement = true,
        Settings = [InterBankingApi.Deadline, InterBankingApi.PixReturn, PixRefund.Mode],
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Form-urlencoded, as the Inter: client_id, client_secret, grant_type, scope (required).
        routes.MapPost("/inter/oauth/v2/token", async (HttpContext http, ConnectorContext ctx) =>
        {
            if (!http.Request.HasFormContentType) return Results.BadRequest(new { error = "invalid_request", error_description = "use application/x-www-form-urlencoded" });
            var form = await http.Request.ReadFormAsync();
            var granted = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(KnownScopes.Contains).Distinct().ToList();
            if (granted.Count == 0) return Results.BadRequest(new { error = "invalid_scope" });
            return SimulatedOAuth.Issue(ctx, form["grant_type"], form["client_id"], form["client_secret"], string.Join(' ', granted));
        });

        // Bacen payload location, created before a cobv (the Inter requires payloadlocation.write).
        routes.MapPost("/inter/pix/v2/loc", (LocRequest req, HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, "payloadlocation.write") ?? (req.TipoCob is "cob" or "cobv"
                ? Results.Json(NewLoc(ctx, http, req.TipoCob), statusCode: 201)
                : Erro.Invalid("tipoCob deve ser cob ou cobv", "tipoCob")));

        Pix.Map(routes, SimulatedOAuth.Refuse, (_, http) => http.Request.Headers[AccountHeader].ToString() is { Length: > 0 } c ? c : null);
        InterCobrancaApi.Map(routes, AccountOf);
        InterBankingApi.Map(routes);
    }

    public async Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        await Pix.OnClockAdvancedAsync(ctx);
        await InterCobrancaApi.OnClockAdvancedAsync(ctx);
        await InterBankingApi.OnClockAdvancedAsync(ctx);
    }

    /// The sandbox account an operation belongs to (x-conta-corrente, or the only Inter account).
    /// No Inter account at all: the operation goes on without an account (no credit).
    internal static (string? Nickname, IResult? Error) AccountOf(ConnectorContext ctx, HttpContext http)
    {
        var header = http.Request.Headers[AccountHeader].ToString();
        var (account, error) = Banking.ForBank(ctx, header);
        if (account is not null) return (account.Nickname, null);
        if (header.Length == 0 && Banking.Accounts(ctx.Store).All(a => a.Bank != ctx.Connector)) return (null, null);
        return (null, Erro.Of(header.Length == 0 ? 400 : 403, "Conta corrente", error!));
    }

    /// A new payload location: sequential id per sandbox, as the cobv embeds it.
    private static PixLoc NewLoc(ConnectorContext ctx, HttpContext http, string tipoCob)
    {
        var loc = new InterLoc
        {
            Id = ctx.Store.All<InterLoc>().Count() + 1, TipoCob = tipoCob, Criacao = ctx.Now,
            Location = $"{http.Request.Host}/qr/v2/{tipoCob}/{Guid.NewGuid():N}",
        };
        ctx.Save(loc.Key, loc);
        return loc.ToPix();
    }

    internal static InterAccountWebhook? AccountWebhook(ConnectorContext ctx, string key) =>
        ctx.Store.Get<InterAccountWebhook>(key) is { WebhookUrl: not null } w ? w : null;

    internal static void SetAccountWebhook(ConnectorContext ctx, string key, string url) =>
        ctx.Save(key, new InterAccountWebhook { Chave = key, WebhookUrl = url, Criacao = ctx.Now });

    internal static bool UnsetAccountWebhook(ConnectorContext ctx, string key)
    {
        if (AccountWebhook(ctx, key) is not { } w) return false;
        ctx.Save(key, new InterAccountWebhook { Chave = key, WebhookUrl = null, Criacao = w.Criacao });
        return true;
    }

    private static IReadOnlyList<TestEvent> TestEvents(ConnectorContext ctx) =>
        Pix.TestEvents(ctx)
            .Concat(AccountWebhook(ctx, InterCobrancaApi.WebhookKey) is { } w ? [new TestEvent(w.WebhookUrl!, InterCobrancaApi.Sample(ctx.Now))] : [])
            .Concat(new[] { InterBankingApi.PixWebhook, InterBankingApi.BoletoWebhook }
                .Where(t => AccountWebhook(ctx, t) is not null)
                .Select(t => new TestEvent(AccountWebhook(ctx, t)!.WebhookUrl!, InterBankingApi.Sample(t, ctx.Now))))
            .ToList();
}

public sealed record LocRequest(string? TipoCob = null);
