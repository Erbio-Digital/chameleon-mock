using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Pix.Common;

/// How one bank exposes the Bacen /v2/cob contract.
public sealed record PixOptions(string BasePath)
{
    /// The PSP removes an active charge at expiration (REMOVIDA_PELO_PSP). False: it stays ATIVA and
    /// can no longer be paid (Inter).
    public bool RemoveOnExpiry { get; init; } = true;

    /// Beyond Bacen: POST /cob (txid by the PSP), PATCH, list and the sandbox payment route.
    public bool ExtendedCobApi { get; init; }

    /// The webhook URL must be https.
    public bool RequireHttpsWebhook { get; init; }

    /// The notification carries componentesValor and the payer (document masked).
    public bool PayerInNotification { get; init; }

    /// Bacen charge with a due date (cobranca com vencimento): PUT/GET /cobv/{txid}.
    public bool CobV { get; init; }

    /// Bacen devolucao: PUT/GET /pix/{e2eid}/devolucao/{id}.
    public bool Devolucao { get; init; }

    /// Scopes of the cobv routes: Bacen reuses cob.*, the Inter has cobv.*.
    public string CobVRead { get; init; } = "cob.read";
    public string CobVWrite { get; init; } = "cob.write";

    /// Bacen GET /pix/{e2eid}: the received Pix, with devolucoes[].
    public bool PixLookup { get; init; }

    /// Payload locations of a cobv (body loc.id; null id = the PSP creates one). Returns null for an unknown id.
    /// Null: the cobv ignores loc.
    public Func<ConnectorContext, HttpContext, int?, PixLoc?>? Locations { get; init; }

    /// The PSP's ISPB, in the ids it generates (rtrId).
    public string Ispb { get; init; } = "00000000";
}

/// The Bacen API Pix (immediate charge plus a webhook per key), shared by every bank that offers it.
/// Generic over the bank's own entity types, so each bank keeps its charges apart in the store.
public sealed partial class PixCobApi<TCob, TWebhook, TDevolucao>
    where TCob : PixCobranca, new()
    where TWebhook : PixKeyWebhook, new()
    where TDevolucao : PixDevolucao, new()
{
    public const string CobRead = "cob.read", CobWrite = "cob.write", WebhookRead = "webhook.read", WebhookWrite = "webhook.write", PixWrite = "pix.write", PixRead = "pix.read";

    private readonly PixOptions _o;

    public PixCobApi(PixOptions options)
    {
        _o = options;
        Cob = new Lifecycle<TCob, CobrancaStatus>("cob", CobrancaStatus.ATIVA, c => c.TxId, c => c.Status, (c, s) => c.Status = s)
            .Allow(CobrancaStatus.ATIVA, CobrancaStatus.CONCLUIDA, "pay")
            .Allow(CobrancaStatus.ATIVA, CobrancaStatus.REMOVIDA_PELO_USUARIO_RECEBEDOR, "remove")
            .Allow(CobrancaStatus.ATIVA, CobrancaStatus.REMOVIDA_PELO_PSP, "ttl");
        Pay = Cob.Action("pay", "Confirm payment", (ctx, c, args) => PixPayment.From(args) is { } p ? PayAsync(ctx, c, p) : Task.FromResult(InvalidPayment),
            new ActionParameter("amount"), new ActionParameter("payerDocument"), new ActionParameter("payerName"), new ActionParameter("payerInfo"));
        Remove = Cob.Action("remove", "Remove charge", (ctx, c, _) => Task.FromResult(Removed(ctx, c)));
        Devolucao = new Lifecycle<TDevolucao, DevolucaoStatus>("devolucao", DevolucaoStatus.EM_PROCESSAMENTO, d => d.Chave, d => d.Status, (d, s) => d.Status = s)
            .Allow(DevolucaoStatus.EM_PROCESSAMENTO, DevolucaoStatus.DEVOLVIDO, "settle")
            .Allow(DevolucaoStatus.EM_PROCESSAMENTO, DevolucaoStatus.NAO_REALIZADO, "reject");
        RefundActions =
        [
            Devolucao.Action("settle", "Settle refund", async (ctx, d, _) => { await SettleRefundAsync(ctx, d); return Results.Ok(ToRefundResource(d)); }),
            Devolucao.Action("reject", "Reject refund", (ctx, d, args) => Task.FromResult(RefuseRefund(ctx, d,
                args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("reason", out var m) && m.GetString() is { Length: > 0 } motivo ? motivo : "recusada pelo simulador")),
                new ActionParameter("reason")),
        ];
    }

    public Lifecycle<TCob, CobrancaStatus> Cob { get; }
    public ConnectorAction Pay { get; }
    public ConnectorAction Remove { get; }

    public EntityDescriptor Describe() => Cob.Describe("txid");

    // ---- Webhook per Pix key: provider routes, control registrations, test events ----

    public IEnumerable<WebhookRegistration> ListWebhooks(ConnectorStore store) =>
        store.All<TWebhook>().Where(w => w.WebhookUrl is not null).Select(w => new WebhookRegistration(w.Chave, w.WebhookUrl!, w.Criacao));

    public void SetWebhook(ConnectorContext ctx, string chave, string url) =>
        ctx.Save(chave, new TWebhook { Chave = chave, WebhookUrl = url, Criacao = ctx.Now });

    // ponytail: removal keeps the row with no URL (the store has no delete yet).
    public bool UnsetWebhook(ConnectorContext ctx, string chave)
    {
        if (ActiveWebhook(ctx, chave) is not { } w) return false;
        ctx.Save(chave, new TWebhook { Chave = chave, WebhookUrl = null, Criacao = w.Criacao });
        return true;
    }

    public TWebhook? ActiveWebhook(ConnectorContext ctx, string chave) =>
        ctx.Store.Get<TWebhook>(chave) is { WebhookUrl: not null } w ? w : null;

    /// One sample Pix notification per registered key webhook.
    public IEnumerable<TestEvent> TestEvents(ConnectorContext ctx) =>
        ctx.Store.All<TWebhook>().Where(w => w.WebhookUrl is not null).Select(w => new TestEvent(w.WebhookUrl!, new
        {
            pix = new[] { new { endToEndId = "E00000000TESTE", txid = "chameleontestevent000000000", chave = w.Chave, valor = "0.01", horario = ctx.Now } },
        }));

    // ---- Provider surface ----

    /// Maps the cob and webhook routes. authorize: null when the bearer has the scope, else the refusal.
    public void Map(IEndpointRouteBuilder routes, Func<ConnectorContext, HttpContext, string, IResult?> authorize,
        Func<ConnectorContext, HttpContext, string?>? accountOf = null)
    {
        var b = _o.BasePath;
        routes.MapPut($"{b}/cob/{{txid}}", (string txid, CriarCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, CobWrite) ?? Create(ctx, http, txid, req, accountOf?.Invoke(ctx, http)));
        routes.MapGet($"{b}/cob/{{txid}}", (string txid, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, CobRead) ?? (ctx.Store.Get<TCob>(txid) is { DataDeVencimento: null } c ? Results.Ok(ToResource(ctx, c)) : CobNotFound(txid)));
        if (_o.CobV) MapCobV(routes, authorize);
        if (_o.Devolucao) MapDevolucao(routes, authorize);
        if (_o.PixLookup)
            routes.MapGet($"{b}/pix/{{e2eid}}", (string e2eid, HttpContext http, ConnectorContext ctx) =>
                authorize(ctx, http, PixRead) ?? (ctx.Store.All<TCob>().FirstOrDefault(c => c.EndToEndId == e2eid) is { } c
                    ? Results.Ok(PixItem(ctx, c))
                    : Problema.NotFound("PixNaoEncontrado", $"pix {e2eid} nao encontrado")));

        routes.MapPut($"{b}/webhook/{{chave}}", (string chave, ConfigurarWebhookRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (authorize(ctx, http, WebhookWrite) is { } refused) return refused;
            if (!WebhookRegistrations.IsValidUrl(req.WebhookUrl) || (_o.RequireHttpsWebhook && !req.WebhookUrl.StartsWith("https://", StringComparison.Ordinal)))
                return Problema.Invalid("WebhookOperacaoInvalida", _o.RequireHttpsWebhook ? "webhookUrl deve ser https" : "webhookUrl invalida", "webhookUrl");
            SetWebhook(ctx, chave, req.WebhookUrl);
            return Results.NoContent();
        });
        routes.MapGet($"{b}/webhook/{{chave}}", (string chave, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, WebhookRead) ?? (ActiveWebhook(ctx, chave) is { } w
                ? Results.Ok(new { webhookUrl = w.WebhookUrl, chave = w.Chave, criacao = w.Criacao })
                : Problema.NotFound("WebhookNaoEncontrado", $"sem webhook para a chave {chave}")));
        routes.MapDelete($"{b}/webhook/{{chave}}", (string chave, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, WebhookWrite) ?? (UnsetWebhook(ctx, chave)
                ? Results.NoContent()
                : Problema.NotFound("WebhookNaoEncontrado", $"sem webhook para a chave {chave}")));

        if (!_o.ExtendedCobApi) return;

        routes.MapPost($"{b}/cob", (CriarCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, CobWrite) ?? Create(ctx, http, NewTxId(), req, accountOf?.Invoke(ctx, http)));
        routes.MapPatch($"{b}/cob/{{txid}}", (string txid, RevisarCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, CobWrite) ?? Revise(ctx, txid, req));
        routes.MapGet($"{b}/cob", (HttpContext http, ConnectorContext ctx) => authorize(ctx, http, CobRead) ?? List(ctx, http.Request.Query));
        // Inter sandbox only: pays the charge as a payer would (the Chameleon is always a sandbox).
        routes.MapPost($"{b}/cob/pagar/{{txid}}", async (string txid, HttpContext http, ConnectorContext ctx) =>
        {
            if (authorize(ctx, http, PixWrite) is { } refused) return refused;
            if (ctx.Store.Get<TCob>(txid) is not { } c) return CobNotFound(txid);
            if (c.Status != CobrancaStatus.ATIVA)
                return Problema.Invalid("CobOperacaoInvalida", $"cobranca em {c.Status}, nao pode ser paga");
            var paid = await PayAsync(ctx, c);
            return paid is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Json(new { e2e = c.EndToEndId }, statusCode: 201) : paid;
        });
        if (!_o.CobV) return;
        // Inter sandbox only: pays the cobv with {"valor": n}, which may differ from the original.
        routes.MapPost($"{b}/cobv/pagar/{{txid}}", async (string txid, HttpContext http, ConnectorContext ctx) =>
        {
            if (authorize(ctx, http, PixWrite) is { } refused) return refused;
            if (ctx.Store.Get<TCob>(txid) is not { DataDeVencimento: not null } c)
                return Problema.NotFound("CobVNaoEncontrado", $"cobranca com vencimento {txid} nao encontrada");
            if (c.Status != CobrancaStatus.ATIVA)
                return Problema.Invalid("CobVOperacaoInvalida", $"cobranca em {c.Status}, nao pode ser paga");
            using var reader = new StreamReader(http.Request.Body);
            var text = await reader.ReadToEndAsync();
            JsonElement? body;
            try { body = string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text).RootElement; }
            catch (JsonException) { return InvalidPayment; }
            if (PixPayment.From(body, PixPayment.ProviderNames) is not { } payment) return InvalidPayment;
            var paid = await PayAsync(ctx, c, payment);
            return paid is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Json(new { e2e = c.EndToEndId }, statusCode: 201) : paid;
        });
    }

    /// Real Pix TTL where the PSP removes an expired active charge.
    public Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        if (!_o.RemoveOnExpiry) return Task.CompletedTask;
        foreach (var c in ctx.Due<TCob>().Where(c => c.Status == CobrancaStatus.ATIVA))
        {
            ctx.Move(Cob, c, CobrancaStatus.REMOVIDA_PELO_PSP, "ttl");
            c.Revisao++;
        }
        return Task.CompletedTask;
    }

    private IResult Create(ConnectorContext ctx, HttpContext http, string txid, CriarCobrancaRequest req, string? conta)
    {
        if (!TxIdPattern().IsMatch(txid))
            return Problema.Invalid("CobOperacaoInvalida", "txid invalido: 26-35 alfanumericos", "txid");
        if (Invalid(req) is { } invalid) return invalid;

        // Same txid -> the existing charge (idempotent), which is what a test needs.
        if (ctx.Store.Get<TCob>(txid) is { } existing)
        {
            if (existing.DataDeVencimento is not null)
                return Problema.Invalid("CobOperacaoInvalida", $"txid {txid} ja utilizado por uma cobranca com vencimento", "txid");
            http.Annotate(note: "idempotent hit");
            return Results.Json(ToResource(ctx, existing), statusCode: 200);
        }

        var header = http.Request.Headers["X-Webhook-Url"].ToString();
        var expiracao = req.Calendario?.Expiracao ?? 86400;
        var cobranca = new TCob
        {
            TxId = txid,
            ValorOriginal = decimal.Parse(req.Valor!.Original!, CultureInfo.InvariantCulture),
            ModalidadeAlteracao = req.Valor.ModalidadeAlteracao ?? 0,
            Chave = req.Chave!,
            ExpiracaoSegundos = expiracao,
            Devedor = req.Devedor,
            SolicitacaoPagador = req.SolicitacaoPagador,
            PixCopiaECola = $"00020126-chameleon-{txid}",
            CriacaoEm = ctx.Now,
            ExpiraEm = ctx.Now.AddSeconds(expiracao),
            AccountNickname = conta,
            WebhookUrl = req.WebhookUrl ?? (string.IsNullOrWhiteSpace(header) ? null : header),
        };
        ctx.Create(Cob, cobranca);
        return Results.Json(ToResource(ctx, cobranca), statusCode: 201);
    }

    private static IResult? Invalid(CriarCobrancaRequest req)
    {
        if (req.Valor?.Original is not { } original || !ValorPattern().IsMatch(original)
            || decimal.Parse(original, CultureInfo.InvariantCulture) == 0)
            return Problema.Invalid("CobOperacaoInvalida", "valor.original deve ser \\d{1,10}.\\d{2} e maior que zero", "valor.original");
        if (req.Valor.ModalidadeAlteracao is not (null or 0 or 1))
            return Problema.Invalid("CobOperacaoInvalida", "valor.modalidadeAlteracao deve ser 0 ou 1", "valor.modalidadeAlteracao");
        if (string.IsNullOrWhiteSpace(req.Chave) || req.Chave.Length > 77)
            return Problema.Invalid("CobOperacaoInvalida", "chave obrigatoria (ate 77 caracteres)", "chave");
        if (req.Calendario is { Expiracao: <= 0 })
            return Problema.Invalid("CobOperacaoInvalida", "calendario.expiracao deve ser maior que zero", "calendario.expiracao");
        if (req.Devedor is { Cpf: not null, Cnpj: not null })
            return Problema.Invalid("CobOperacaoInvalida", "devedor com cpf e cnpj ao mesmo tempo", "devedor");
        return null;
    }

    private IResult Revise(ConnectorContext ctx, string txid, RevisarCobrancaRequest req)
    {
        if (ctx.Store.Get<TCob>(txid) is not { } c) return CobNotFound(txid);
        if (req.Status is { } status)
        {
            if (status != nameof(CobrancaStatus.REMOVIDA_PELO_USUARIO_RECEBEDOR) || req.Valor is not null || req.Calendario is not null || req.Devedor is not null)
                return Problema.Invalid("CobOperacaoInvalida", "status so aceita REMOVIDA_PELO_USUARIO_RECEBEDOR, sem outras alteracoes", "status");
            if (c.Status != CobrancaStatus.ATIVA) return Problema.Invalid("CobOperacaoInvalida", $"cobranca em {c.Status}");
            return Removed(ctx, c);
        }
        if (c.Status != CobrancaStatus.ATIVA) return Problema.Invalid("CobOperacaoInvalida", $"cobranca em {c.Status}, nao pode ser revisada");
        if (req.Valor?.Original is { } original)
        {
            if (!ValorPattern().IsMatch(original) || decimal.Parse(original, CultureInfo.InvariantCulture) == 0)
                return Problema.Invalid("CobOperacaoInvalida", "valor.original invalido", "valor.original");
            c.ValorOriginal = decimal.Parse(original, CultureInfo.InvariantCulture);
        }
        if (req.Calendario is { } cal)
        {
            if (cal.Expiracao <= 0) return Problema.Invalid("CobOperacaoInvalida", "calendario.expiracao deve ser maior que zero", "calendario.expiracao");
            c.ExpiracaoSegundos = cal.Expiracao;
            c.ExpiraEm = c.CriacaoEm.AddSeconds(cal.Expiracao);
        }
        if (req.Devedor is not null) c.Devedor = req.Devedor;
        if (req.SolicitacaoPagador is not null) c.SolicitacaoPagador = req.SolicitacaoPagador;
        c.Revisao++;
        ctx.Save(c.TxId, c);
        return Results.Ok(ToResource(ctx, c));
    }

    private IResult List(ConnectorContext ctx, IQueryCollection q)
    {
        if (!DateTimeOffset.TryParse(q["inicio"], CultureInfo.InvariantCulture, out var inicio)
            || !DateTimeOffset.TryParse(q["fim"], CultureInfo.InvariantCulture, out var fim))
            return Problema.Invalid("CobConsultaInvalida", "inicio e fim obrigatorios (RFC 3339)", "inicio");
        var status = q["status"].ToString();
        var all = ctx.Store.All<TCob>()
            .Where(c => c.CriacaoEm >= inicio && c.CriacaoEm <= fim)
            .Where(c => status.Length == 0 || c.Status.ToString() == status)
            .Where(c => q["cpf"].ToString() is not { Length: > 0 } cpf || c.Devedor?.Cpf == cpf)
            .Where(c => q["cnpj"].ToString() is not { Length: > 0 } cnpj || c.Devedor?.Cnpj == cnpj)
            .OrderBy(c => c.CriacaoEm).ToList();
        var page = int.TryParse(q["paginacao.paginaAtual"], out var p) ? Math.Max(p, 0) : 0;
        var size = int.TryParse(q["paginacao.itensPorPagina"], out var s) ? Math.Clamp(s, 1, 1000) : 100;
        return Results.Ok(new
        {
            parametros = new
            {
                inicio, fim,
                paginacao = new { paginaAtual = page, itensPorPagina = size, quantidadeDePaginas = (all.Count + size - 1) / size, quantidadeTotalDeItens = all.Count },
            },
            cobs = all.Skip(page * size).Take(size).Select(c => ToResource(ctx, c)),
        });
    }

    /// Pays the charge as a payer would: the amount paid (default the original), the payer (default
    /// the debtor) and the payer's free text, then credits the key's account and notifies its webhook.
    public async Task<IResult> PayAsync(ConnectorContext ctx, TCob cobranca, PixPayment? payment = null)
    {
        if (!_o.RemoveOnExpiry && ctx.Now >= cobranca.ExpiraEm)
            return Problema.Invalid("CobOperacaoInvalida", "cobranca expirada nao pode ser paga");
        ctx.Move(Cob, cobranca, CobrancaStatus.CONCLUIDA);
        cobranca.Revisao++;
        cobranca.EndToEndId = "E" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToUpperInvariant()[..31];
        cobranca.PagoEm = ctx.Now;
        cobranca.ValorPago = payment?.Valor ?? cobranca.ValorOriginal;
        cobranca.Pagador = payment?.Pagador ?? (cobranca.Devedor is { Cpf: not null } or { Cnpj: not null } ? cobranca.Devedor : null);
        cobranca.InfoPagador = payment?.InfoPagador;
        Banking.CreditPix(ctx, cobranca.Chave, cobranca.ValorPago.Value, cobranca.EndToEndId,
            new() { ["endToEndId"] = cobranca.EndToEndId, ["txId"] = cobranca.TxId });

        await NotifyKeyAsync(ctx, cobranca.TxId, cobranca, new { pix = new[] { Notification(ctx, cobranca) } });
        return Results.Ok(ToResource(ctx, cobranca));
    }

    /// Where the PSP notifies: the Pix key's webhook. The charge's simulator-only URL only counts
    /// without a subscription (local use in "default"). Without one, the journal names the charge's key
    /// and the keys that do have a webhook (operators often register another key).
    private Task NotifyKeyAsync(ConnectorContext ctx, string entityId, TCob cob, object payload)
    {
        var url = ActiveWebhook(ctx, cob.Chave)?.WebhookUrl ?? (ctx.Subscription is null ? cob.WebhookUrl : null);
        if (url is not null) return ctx.NotifyAsync(entityId, url, payload);
        var registered = ListWebhooks(ctx.Store).Select(w => ShownKey(w.Key)).ToList();
        return ctx.NotifyAsync(entityId, null, payload, $"no webhook registered for key {ShownKey(cob.Chave)}; "
            + (registered.Count > 0 ? $"keys with a webhook: {string.Join(", ", registered)}" : "no key with a webhook in this sandbox"));
    }

    // A random key (EVP) identifies no one; CPF/CNPJ, e-mail and phone keys are masked as in the journal.
    private static string ShownKey(string chave) => Guid.TryParseExact(chave, "D", out _) ? chave : PayloadSanitizer.Mask(chave);

    private object Notification(ConnectorContext ctx, TCob c)
    {
        if (!_o.PayerInNotification) return PixItem(ctx, c);
        var valor = Banking.Money(c.ValorPago ?? c.ValorOriginal);
        var item = new Dictionary<string, object?>
        {
            ["endToEndId"] = c.EndToEndId, ["txid"] = c.TxId, ["chave"] = c.Chave, ["valor"] = valor, ["horario"] = ctx.Now,
            ["componentesValor"] = new { original = new { valor } },
            // The payer, document masked as the PSP sends it.
            ["pagador"] = new { nome = c.Pagador?.Nome ?? c.Devedor?.Nome ?? "PAGADOR SIMULADO", cpfCnpj = Masked(c.Pagador?.Cpf ?? c.Pagador?.Cnpj ?? c.Devedor?.Cpf ?? c.Devedor?.Cnpj) },
        };
        if (c.EndToEndId is not null && Refunds(ctx, c.EndToEndId).Select(ToRefundResource).ToList() is { Count: > 0 } devolucoes)
            item["devolucoes"] = devolucoes;
        return item;
    }

    /// The received Pix as the Bacen API shows it in pix[] and in the webhook (absent fields omitted).
    private Dictionary<string, object?> PixItem(ConnectorContext ctx, TCob c)
    {
        var valor = Banking.Money(c.ValorPago ?? c.ValorOriginal);
        var item = new Dictionary<string, object?>
        {
            ["endToEndId"] = c.EndToEndId, ["txid"] = c.TxId, ["valor"] = valor, ["chave"] = c.Chave, ["horario"] = c.PagoEm,
            ["componentesValor"] = new { original = new { valor } },
        };
        if (c.Pagador is { } p)
            item["pagador"] = p.Cpf is not null ? new { cpf = p.Cpf, nome = p.Nome } : new { cnpj = p.Cnpj, nome = p.Nome };
        if (c.InfoPagador is not null) item["infoPagador"] = c.InfoPagador;
        if (c.EndToEndId is not null && Refunds(ctx, c.EndToEndId).Select(ToRefundResource).ToList() is { Count: > 0 } devolucoes)
            item["devolucoes"] = devolucoes;
        return item;
    }

    private static readonly IResult InvalidPayment =
        Results.BadRequest(new { message = "amount (valor): a decimal greater than zero; payer document: CPF (11) or CNPJ (14) digits" });

    private IResult Removed(ConnectorContext ctx, TCob c)
    {
        ctx.Move(Cob, c, CobrancaStatus.REMOVIDA_PELO_USUARIO_RECEBEDOR);
        c.Revisao++;
        return Results.Ok(ToResource(ctx, c));
    }

    private static IResult CobNotFound(string txid) => Problema.NotFound("CobNaoEncontrado", $"cobranca {txid} nao encontrada");


    private static string Masked(string? document) => document is { Length: >= 6 }
        ? new string('*', 3) + document[3..^2] + "**"
        : "***.***.***-**";

    private static string NewTxId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private object ToResource(ConnectorContext ctx, TCob c) => new
    {
        calendario = new { criacao = c.CriacaoEm, expiracao = c.ExpiracaoSegundos },
        txid = c.TxId,
        revisao = c.Revisao,
        status = c.Status.ToString(),
        valor = new { original = Banking.Money(c.ValorOriginal), modalidadeAlteracao = c.ModalidadeAlteracao },
        chave = c.Chave,
        devedor = c.Devedor,
        solicitacaoPagador = c.SolicitacaoPagador,
        pixCopiaECola = c.PixCopiaECola,
        pix = c.Status == CobrancaStatus.CONCLUIDA ? new object[] { PixItem(ctx, c) } : [],
    };

    [GeneratedRegex("^[a-zA-Z0-9]{26,35}$")] private static partial Regex TxIdPattern();
    [GeneratedRegex(@"^\d{1,10}\.\d{2}$")] private static partial Regex ValorPattern();
}

// ---- Request DTOs mirroring the real Pix payload ----

public sealed record CriarCobrancaRequest(
    CalendarioRequest? Calendario = null,
    ValorRequest? Valor = null,
    string? Chave = null,
    Devedor? Devedor = null,
    string? SolicitacaoPagador = null,
    // Simulator-only fallback when the Pix key has no webhook configured (not a real Pix field).
    string? WebhookUrl = null);

public sealed record RevisarCobrancaRequest(
    CalendarioRequest? Calendario = null, ValorRequest? Valor = null, Devedor? Devedor = null, string? SolicitacaoPagador = null, string? Status = null);

public sealed record CalendarioRequest(int Expiracao = 86400);
public sealed record ValorRequest(string? Original = null, int? ModalidadeAlteracao = null);

/// How a payer pays a charge (the "pay" action, sandbox payment routes); nulls keep the charge's own.
public sealed record PixPayment(decimal? Valor = null, Devedor? Pagador = null, string? InfoPagador = null)
{
    /// Argument names: the action's own, or the provider's on its sandbox payment route.
    public sealed record Names(string Amount, string Document, string Name, string Info);
    public static readonly Names ActionNames = new("amount", "payerDocument", "payerName", "payerInfo");
    public static readonly Names ProviderNames = new("valor", "pagadorDocumento", "pagadorNome", "infoPagador");

    /// From the arguments; null when an argument is invalid.
    public static PixPayment? From(JsonElement? args, Names? names = null)
    {
        var keys = names ?? ActionNames;
        if (args is not { ValueKind: JsonValueKind.Object } a) return new();
        decimal? valor = null;
        if (a.TryGetProperty(keys.Amount, out var v) && v.ValueKind != JsonValueKind.Null)
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) valor = n;
            else if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) valor = s;
            else if (!(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))) return null;
        }
        if (valor is <= 0) return null;
        var documento = Text(a, keys.Document) is { } d ? new string(d.Where(char.IsAsciiDigit).ToArray()) : null;
        if (documento is not (null or { Length: 11 or 14 })) return null;
        var nome = Text(a, keys.Name);
        var pagador = documento is null && nome is null ? null : new Devedor
        {
            Cpf = documento is { Length: 11 } ? documento : null,
            Cnpj = documento is { Length: 14 } ? documento : null,
            Nome = nome ?? "PAGADOR SIMULADO",
        };
        return new(valor is { } x ? Math.Round(x, 2) : null, pagador, Text(a, keys.Info));
    }

    private static string? Text(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
public sealed record ConfigurarWebhookRequest(string WebhookUrl);
