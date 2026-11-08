using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Bradesco;

/// Bradesco under one root for the four API contexts: the two token
/// routes, Bacen Pix (cob, cobv, devolucao, webhook per key), the nonprod payment simulation, SPI
/// (outgoing Pix), TED, balance and statement over the sandbox ledger.
public sealed class BradescoConnector : IConnector
{
    public const string ConnectorName = "bradesco";
    private const string Scope = "cob.read cob.write pix.read pix.write webhook.read webhook.write";

    internal static readonly PixCobApi<Cobranca, PixWebhook, Devolucao> Pix =
        new(new PixOptions("/bradesco/v2") { CobV = true, Devolucao = true, Ispb = "60746948" });

    public string Name => ConnectorName;

    // ponytail: Bacen does not publish the PSP retry schedule; 5 attempts doubling from 2 s is our default.
    private static readonly WebhookPolicy Policy = new(MaxAttempts: 5, BaseDelayMs: 2000, TimeoutMs: 10000);

    private static readonly WebhookRegistrations Registrations = new("Pix key", Pix.ListWebhooks, Pix.SetWebhook, Pix.UnsetWebhook);

    public ConnectorDescriptor Describe() => new(Name, [Pix.Describe(), Pix.DescribeRefund(), BradescoSpiApi.Transferencia.Describe("idTransacao"), BradescoTedApi.Transferencia.Describe("chave")],
        [Pix.Pay, Pix.Remove, .. Pix.RefundActions, .. BradescoSpiApi.Actions, .. BradescoTedApi.Actions], Policy, Registrations)
    {
        Title = "Bradesco",
        Category = "banking",
        Description = "Pix (cob, cobv, refund and webhook), outgoing Pix over SPI, TED, balance and statement, with OAuth and mTLS.",
        Credentials = SimulatedOAuth.Credentials("/v2/oauth/token"),
        // {"pix":[]}: the activation a webhook receiver accepts; a sample Pix would send the app after a charge it never created.
        TestEvents = ctx => Pix.ListWebhooks(ctx.Store).Select(w => new TestEvent(w.Url, new { pix = Array.Empty<object>() })).ToList(),
        Settings = [PixRefund.Mode, BradescoSpiApi.Mode, BradescoTedApi.Mode],
        HasAccounts = true,
        ClientCertificate = true,
        WebhookCertificate = true,
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Pix and SPI token (QRCODEPIX, TRANSFER): form grant_type, the client in HTTP Basic.
        routes.MapPost("/bradesco/v2/oauth/token", async (HttpContext http, ConnectorContext ctx) =>
        {
            if (!http.Request.HasFormContentType) return FormRequired;
            var form = await http.Request.ReadFormAsync();
            var (id, secret) = SimulatedOAuth.BasicClient(http.Request);
            return SimulatedOAuth.Issue(ctx, form["grant_type"], id, secret, Scope);
        });

        // Account and TED token (ACCOUNT, TED): form with grant_type, client_id and client_secret.
        routes.MapPost("/bradesco/auth/server-mtls/v2/token", async (HttpContext http, ConnectorContext ctx) =>
        {
            if (!http.Request.HasFormContentType) return FormRequired;
            var form = await http.Request.ReadFormAsync();
            return SimulatedOAuth.Issue(ctx, form["grant_type"], form["client_id"], form["client_secret"], Scope);
        });

        Pix.Map(routes, SimulatedOAuth.Refuse);
        BradescoSpiApi.Map(routes);
        BradescoTedApi.Map(routes);
        BradescoContaApi.Map(routes);

        // Simulation only (not a bank API; the Bradesco sandbox refuses it): pays the charge of an EMV
        // as a payer would; client applications call it in test environments to mark a charge as paid.
        routes.MapPost("/bradesco/simulate/pix-payment-by-emv", async (SimularPagamentoRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, PixCobApi<Cobranca, PixWebhook, Devolucao>.PixWrite) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(req.Emv)) return Problema.Invalid("CobOperacaoInvalida", "emv obrigatorio", "emv");
            if (ctx.Store.All<Cobranca>().FirstOrDefault(c => c.PixCopiaECola == req.Emv) is not { } cob)
                return Problema.NotFound("CobNaoEncontrado", "nenhuma cobranca com este emv");
            http.Annotate(entityId: cob.TxId);
            if (cob.Status != CobrancaStatus.ATIVA) return Problema.Invalid("CobOperacaoInvalida", $"cobranca em {cob.Status}, nao pode ser paga");
            if (req.Valor is <= 0) return Problema.Invalid("CobOperacaoInvalida", "valor deve ser maior que zero", "valor");
            var pagador = req.Pagador is { } p && (p.Cpf ?? p.Cnpj) is not null ? new Devedor { Cpf = p.Cpf, Cnpj = p.Cnpj, Nome = p.Nome } : null;
            return await Pix.PayAsync(ctx, cob, new PixPayment(req.Valor is { } v ? Math.Round(v, 2) : null, pagador, req.InfoPagador));
        });

        // Legacy action routes, aliases of the generic action route.
        routes.MapPost("/_chameleon/bradesco/cob/{txid}/pay", (string txid, ConnectorContext ctx) => Pix.Pay.Run(ctx, txid, null));
        routes.MapPost("/_chameleon/bradesco/cob/{txid}/remove", (string txid, ConnectorContext ctx) => Pix.Remove.Run(ctx, txid, null));
    }

    /// Real Pix TTL: an ATIVA charge past expiration is removed by the PSP.
    public Task OnClockAdvancedAsync(ConnectorContext ctx) => Pix.OnClockAdvancedAsync(ctx);

    /// The token grants every Bradesco API, so only the bearer itself is checked.
    internal static IResult? Refuse(ConnectorContext ctx, HttpContext http) =>
        SimulatedOAuth.Check(ctx, http, null) == SimulatedOAuth.Access.Granted ? null : Results.Unauthorized();

    /// The sandbox account with this agency and account number, with or without the check digit
    /// (clients send both); thisBank false also finds accounts of other banks (a transfer's destination).
    internal static BankAccount? FindAccount(ConnectorContext ctx, string? agencia, string? conta, bool thisBank)
    {
        if (Digits(conta).TrimStart('0').Length == 0) return null;
        return Banking.Accounts(ctx.Store)
            .Where(a => !thisBank || a.Bank == ctx.Connector)
            .Where(a => string.IsNullOrEmpty(agencia) || Digits(a.Branch).TrimStart('0') == Digits(agencia).TrimStart('0'))
            .FirstOrDefault(a => Banking.AccountMatches(a.Account, conta!));
    }

    /// A JSON object without the absent fields, as the bank omits them.
    internal static Dictionary<string, object?> Compact(params (string Key, object? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value);

    private static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());

    private static readonly IResult FormRequired =
        Results.BadRequest(new { error = "invalid_request", error_description = "use application/x-www-form-urlencoded" });
}

public sealed record SimularPagamentoRequest(string? Emv = null, decimal? Valor = null, Devedor? Pagador = null, string? InfoPagador = null);
