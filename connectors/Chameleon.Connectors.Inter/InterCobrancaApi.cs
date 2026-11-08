using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Inter;

/// Inter Cobranca v3 (boleto with Pix): asynchronous issuance, query, list, summary, cancel,
/// sandbox payment, one webhook per account and the lifecycle on the sandbox clock.
internal static class InterCobrancaApi
{
    public const string Read = "boleto-cobranca.read", Write = "boleto-cobranca.write";
    public const string WebhookKey = "cobranca";
    private const string Base = "/inter/cobranca/v3/cobrancas";
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(30);

    public static readonly Lifecycle<InterCobranca, SituacaoCobranca> Cobranca =
        new Lifecycle<InterCobranca, SituacaoCobranca>("cobranca", SituacaoCobranca.EM_PROCESSAMENTO,
                c => c.CodigoSolicitacao, c => c.Situacao, (c, s) => c.Situacao = s)
            .Allow(SituacaoCobranca.EM_PROCESSAMENTO, SituacaoCobranca.A_RECEBER, "issue")
            .Allow(SituacaoCobranca.EM_PROCESSAMENTO, SituacaoCobranca.FALHA_EMISSAO, "fail-issuance")
            .Allow(SituacaoCobranca.A_RECEBER, SituacaoCobranca.ATRASADO, "make-overdue")
            .Allow(SituacaoCobranca.A_RECEBER, SituacaoCobranca.RECEBIDO, "pay")
            .Allow(SituacaoCobranca.ATRASADO, SituacaoCobranca.RECEBIDO, "pay")
            .Allow(SituacaoCobranca.A_RECEBER, SituacaoCobranca.MARCADO_RECEBIDO, "mark-received")
            .Allow(SituacaoCobranca.ATRASADO, SituacaoCobranca.MARCADO_RECEBIDO, "mark-received")
            .Allow(SituacaoCobranca.A_RECEBER, SituacaoCobranca.CANCELADO, "cancel")
            .Allow(SituacaoCobranca.ATRASADO, SituacaoCobranca.CANCELADO, "cancel")
            .Allow(SituacaoCobranca.A_RECEBER, SituacaoCobranca.EXPIRADO, "ttl")
            .Allow(SituacaoCobranca.ATRASADO, SituacaoCobranca.EXPIRADO, "ttl");

    public static readonly ConnectorAction[] Actions =
    [
        Cobranca.Action("issue", "Complete issuance", async (ctx, c, _) => { await IssueAsync(ctx, c); return Results.Ok(ToResource(c)); }),
        Cobranca.Action("fail-issuance", "Fail issuance", async (ctx, c, _) =>
        {
            await MoveAsync(ctx, c, SituacaoCobranca.FALHA_EMISSAO, notify: true);
            return Results.Ok(ToResource(c));
        }),
        Cobranca.Action("make-overdue", "Make overdue (ATRASADO)", async (ctx, c, _) =>
        {
            await MoveAsync(ctx, c, SituacaoCobranca.ATRASADO, notify: false);
            return Results.Ok(ToResource(c));
        }),
        Cobranca.Action("pay", "Pay", async (ctx, c, args) =>
        {
            var valor = decimal.TryParse(Arg(args, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : (decimal?)null;
            await ReceiveAsync(ctx, c, Arg(args, "payWith") == "PIX" ? "PIX" : "BOLETO", valor);
            return Results.Ok(ToResource(c));
        }, new ActionParameter("payWith"), new ActionParameter("amount")),
        Cobranca.Action("mark-received", "Mark as received", async (ctx, c, _) =>
        {
            await MoveAsync(ctx, c, SituacaoCobranca.MARCADO_RECEBIDO, notify: true);
            return Results.Ok(ToResource(c));
        }),
        Cobranca.Action("cancel", "Cancel", async (ctx, c, args) =>
        {
            c.MotivoCancelamento = Arg(args, "reason") ?? "cancelada pelo simulador";
            await MoveAsync(ctx, c, SituacaoCobranca.CANCELADO, notify: true);
            return Results.Ok(ToResource(c));
        }, new ActionParameter("reason")),
    ];

    public static void Map(IEndpointRouteBuilder routes, Func<ConnectorContext, HttpContext, (string? Nickname, IResult? Error)> accountOf)
    {
        routes.MapPost(Base, (EmitirCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Write) is { } refused) return refused;
            if (Invalid(ctx, req) is { } invalid) return invalid;
            var (conta, error) = accountOf(ctx, http);
            if (error is not null) return error;

            var valor = Math.Round(req.ValorNominal!.Value, 2);
            var vencimento = DateOnly.ParseExact(req.DataVencimento!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Same request within 30 min: the same charge (the Inter does not document it).
            if (ctx.Store.All<InterCobranca>().FirstOrDefault(c => c.SeuNumero == req.SeuNumero && c.ValorNominal == valor
                    && c.DataVencimento == vencimento && c.Pagador.CpfCnpj == req.Pagador!.CpfCnpj && ctx.Now - c.CriadoEm < IdempotencyWindow) is { } same)
            {
                http.Annotate(note: "idempotent hit");
                return Results.Ok(new { codigoSolicitacao = same.CodigoSolicitacao });
            }

            var codigo = Guid.NewGuid().ToString();
            var txid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var digits = Digits(43);
            var cobranca = new InterCobranca
            {
                CodigoSolicitacao = codigo,
                SeuNumero = req.SeuNumero!,
                ValorNominal = valor,
                DataVencimento = vencimento,
                NumDiasAgenda = req.NumDiasAgenda ?? 0,
                Pagador = req.Pagador!,
                Mensagem = req.Mensagem?.Linha1,
                CriadoEm = ctx.Now,
                DataSituacao = ctx.Now,
                NossoNumero = Digits(11),
                CodigoBarras = "0779" + digits[..40],
                LinhaDigitavel = "0779" + digits,
                TxId = txid,
                PixCopiaECola = $"00020101021226-chameleon-inter-{txid}",
                AccountNickname = conta,
            };
            ctx.Create(Cobranca, cobranca);
            return Results.Ok(new { codigoSolicitacao = codigo });
        });

        routes.MapGet($"{Base}/{{codigo}}", (string codigo, HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, Read) ?? (Find(ctx, codigo) is { } c ? Results.Ok(ToResource(c)) : NotFound(codigo)));

        routes.MapGet($"{Base}/{{codigo}}/pdf", (string codigo, HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, Read) ?? (Find(ctx, codigo) is { } c
                ? Results.Ok(new { pdf = Convert.ToBase64String(Encoding.ASCII.GetBytes($"%PDF-1.4\n% boleto simulated by Chameleon {c.CodigoSolicitacao}\n%%EOF\n")) })
                : NotFound(codigo)));

        routes.MapGet(Base, (HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Read) is { } refused) return refused;
            if (Filter(ctx, http.Request.Query) is not { } all) return Erro.Invalid("dataInicial e dataFinal obrigatorias (yyyy-MM-dd)", "dataInicial");
            var q = http.Request.Query;
            var page = int.TryParse(q["paginacao.paginaAtual"], out var p) ? Math.Max(p, 0) : 0;
            var size = int.TryParse(q["paginacao.itensPorPagina"], out var s) ? Math.Clamp(s, 1, 1000) : 100;
            var items = all.Skip(page * size).Take(size).Select(ToResource).ToList();
            var pages = (all.Count + size - 1) / size;
            return Results.Ok(new
            {
                totalPaginas = pages, totalElementos = all.Count, ultimaPagina = page >= pages - 1, primeiraPagina = page == 0,
                tamanhoPagina = size, numeroDeElementos = items.Count, cobrancas = items,
            });
        });

        routes.MapGet($"{Base}/sumario", (HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Read) is { } refused) return refused;
            if (Filter(ctx, http.Request.Query) is not { } all) return Erro.Invalid("dataInicial e dataFinal obrigatorias (yyyy-MM-dd)", "dataInicial");
            return Results.Ok(all.GroupBy(c => c.Situacao)
                .Select(g => new { situacao = g.Key.ToString(), valor = Banking.Money(g.Sum(c => c.ValorNominal)), quantidade = g.Count() }));
        });

        routes.MapPost($"{Base}/{{codigo}}/cancelar", async (string codigo, CancelarCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Write) is { } refused) return refused;
            if (Find(ctx, codigo) is not { } c) return NotFound(codigo);
            if (string.IsNullOrWhiteSpace(req.MotivoCancelamento) || req.MotivoCancelamento.Length > 50)
                return Erro.Invalid("motivoCancelamento obrigatorio (ate 50 caracteres)", "motivoCancelamento");
            if (!Cobranca.CanMove(c.Situacao, SituacaoCobranca.CANCELADO)) return Erro.Invalid($"cobranca em {c.Situacao} nao pode ser cancelada");
            c.MotivoCancelamento = req.MotivoCancelamento;
            await MoveAsync(ctx, c, SituacaoCobranca.CANCELADO, notify: true);
            return Results.StatusCode(202);
        });

        // Inter sandbox only: pays the boleto as a payer would, by boleto or by its Pix.
        routes.MapPost($"{Base}/{{codigo}}/pagar", async (string codigo, PagarCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Write) is { } refused) return refused;
            if (Find(ctx, codigo) is not { } c) return NotFound(codigo);
            if (req.PagarCom is not ("BOLETO" or "PIX")) return Erro.Invalid("pagarCom deve ser BOLETO ou PIX", "pagarCom");
            if (!Cobranca.CanMove(c.Situacao, SituacaoCobranca.RECEBIDO)) return Erro.Invalid($"cobranca em {c.Situacao} nao pode ser paga");
            await ReceiveAsync(ctx, c, req.PagarCom);
            return Results.NoContent();
        });

        routes.MapPut($"{Base}/webhook", (ConfigurarWebhookCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, Write) is { } refused) return refused;
            if (!WebhookRegistrations.IsValidUrl(req.WebhookUrl) || !req.WebhookUrl.StartsWith("https://", StringComparison.Ordinal))
                return Erro.Invalid("webhookUrl deve ser https", "webhookUrl");
            InterConnector.SetAccountWebhook(ctx, WebhookKey, req.WebhookUrl);
            return Results.NoContent();
        });
        routes.MapGet($"{Base}/webhook", (HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, Read) ?? (InterConnector.AccountWebhook(ctx, WebhookKey) is { } w
                ? Results.Ok(new { webhookUrl = w.WebhookUrl, criacao = w.Criacao })
                : Erro.Of(404, "Webhook nao encontrado", "nenhum webhook de cobranca cadastrado")));
        routes.MapDelete($"{Base}/webhook", (HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, Write) ?? (InterConnector.UnsetAccountWebhook(ctx, WebhookKey)
                ? Results.NoContent()
                : Erro.Of(404, "Webhook nao encontrado", "nenhum webhook de cobranca cadastrado")));
    }

    /// Issuance completes on the next tick or advance; then due date (ATRASADO) and expiry.
    public static async Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        foreach (var c in ctx.Due<InterCobranca>())
        {
            if (c.Situacao == SituacaoCobranca.EM_PROCESSAMENTO) await IssueAsync(ctx, c);
            if (c.Situacao == SituacaoCobranca.A_RECEBER && ctx.Now >= c.AtrasaEm)
                await MoveAsync(ctx, c, SituacaoCobranca.ATRASADO, notify: false, reason: "vencimento");
            if (c.Situacao is SituacaoCobranca.A_RECEBER or SituacaoCobranca.ATRASADO && ctx.Now >= c.ExpiraEm)
                await MoveAsync(ctx, c, SituacaoCobranca.EXPIRADO, notify: true, reason: "ttl");
        }
    }

    public static object Sample(DateTimeOffset now) => new[]
    {
        new
        {
            codigoSolicitacao = "00000000-0000-0000-0000-000000000000", seuNumero = "TESTE", situacao = "RECEBIDO",
            dataHoraSituacao = now, valorTotalRecebido = "0.01", origemRecebimento = "BOLETO", nossoNumero = "00000000000",
            codigoBarras = new string('0', 44), linhaDigitavel = new string('0', 47), txid = "chameleontestevent000000000", pixCopiaECola = "",
        },
    };

    private static Task IssueAsync(ConnectorContext ctx, InterCobranca c) => MoveAsync(ctx, c, SituacaoCobranca.A_RECEBER, notify: true);

    /// valor: amount actually paid (a boleto paid short, from an action); default the nominal value.
    public static async Task ReceiveAsync(ConnectorContext ctx, InterCobranca c, string origem, decimal? valor = null)
    {
        c.ValorTotalRecebido = valor ?? c.ValorNominal;
        c.OrigemRecebimento = origem;
        await MoveAsync(ctx, c, SituacaoCobranca.RECEBIDO, notify: true);
        if (c.AccountNickname is not null && ctx.Store.Get<BankAccount>(c.AccountNickname) is { } account)
            Banking.Credit(ctx, account, c.ValorTotalRecebido, origem == "PIX" ? "Pix recebido (boleto)" : "Boleto recebido", c.CodigoSolicitacao,
                new() { ["codigoSolicitacao"] = c.CodigoSolicitacao, ["seuNumero"] = c.SeuNumero });
    }

    private static async Task MoveAsync(ConnectorContext ctx, InterCobranca c, SituacaoCobranca to, bool notify, string? reason = null)
    {
        ctx.Move(Cobranca, c, to, reason);
        c.DataSituacao = ctx.Now;
        ctx.Save(c.CodigoSolicitacao, c);
        if (!notify) return;
        await ctx.NotifyAsync(c.CodigoSolicitacao, InterConnector.AccountWebhook(ctx, WebhookKey)?.WebhookUrl, new[] { Callback(c) });
    }

    private static object Callback(InterCobranca c) => new
    {
        codigoSolicitacao = c.CodigoSolicitacao,
        seuNumero = c.SeuNumero,
        situacao = c.Situacao.ToString(),
        dataHoraSituacao = c.DataSituacao,
        valorTotalRecebido = Banking.Money(c.ValorTotalRecebido),
        origemRecebimento = c.OrigemRecebimento,
        nossoNumero = c.NossoNumero,
        codigoBarras = c.CodigoBarras,
        linhaDigitavel = c.LinhaDigitavel,
        txid = c.TxId,
        pixCopiaECola = c.PixCopiaECola,
    };

    private static bool Issued(InterCobranca c) => c.Situacao is not (SituacaoCobranca.EM_PROCESSAMENTO or SituacaoCobranca.FALHA_EMISSAO);

    private static object ToResource(InterCobranca c) => new
    {
        cobranca = new
        {
            codigoSolicitacao = c.CodigoSolicitacao,
            seuNumero = c.SeuNumero,
            dataEmissao = Brasilia.DateOf(c.CriadoEm).ToString("yyyy-MM-dd"),
            dataVencimento = c.DataVencimento.ToString("yyyy-MM-dd"),
            // A number, as the Inter answers it (clients read a double).
            valorNominal = c.ValorNominal,
            tipoCobranca = "SIMPLES",
            situacao = c.Situacao.ToString(),
            dataSituacao = Brasilia.DateOf(c.DataSituacao).ToString("yyyy-MM-dd"),
            valorTotalRecebido = Banking.Money(c.ValorTotalRecebido),
            origemRecebimento = c.OrigemRecebimento,
            motivoCancelamento = c.MotivoCancelamento,
            arquivada = false,
            pagador = c.Pagador,
        },
        // Barcode and Pix exist only once issued.
        boleto = Issued(c) ? new { nossoNumero = c.NossoNumero, codigoBarras = c.CodigoBarras, linhaDigitavel = c.LinhaDigitavel } : null,
        pix = Issued(c) ? new { txid = c.TxId, pixCopiaECola = c.PixCopiaECola } : null,
    };

    private static List<InterCobranca>? Filter(ConnectorContext ctx, IQueryCollection q)
    {
        if (!DateOnly.TryParseExact(q["dataInicial"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var inicio)
            || !DateOnly.TryParseExact(q["dataFinal"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fim))
            return null;
        var por = q["filtrarDataPor"].ToString() is { Length: > 0 } f ? f : "VENCIMENTO";
        var situacoes = q["situacao"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DateOnly? DateOf(InterCobranca c) => por switch
        {
            "EMISSAO" => Brasilia.DateOf(c.CriadoEm),
            "PAGAMENTO" => c.Situacao is SituacaoCobranca.RECEBIDO or SituacaoCobranca.MARCADO_RECEBIDO ? Brasilia.DateOf(c.DataSituacao) : null,
            _ => c.DataVencimento,
        };
        return ctx.Store.All<InterCobranca>()
            .Where(c => DateOf(c) is { } d && d >= inicio && d <= fim)
            .Where(c => situacoes.Length == 0 || situacoes.Contains(c.Situacao.ToString()))
            .Where(c => q["seuNumero"].ToString() is not { Length: > 0 } n || c.SeuNumero == n)
            .Where(c => q["cpfCnpjPessoaPagadora"].ToString() is not { Length: > 0 } doc || c.Pagador.CpfCnpj == doc)
            .OrderBy(c => c.DataVencimento).ThenBy(c => c.CriadoEm).ToList();
    }

    private static IResult? Invalid(ConnectorContext ctx, EmitirCobrancaRequest req)
    {
        if (req.SeuNumero is not { Length: >= 1 and <= 15 }) return Erro.Invalid("seuNumero obrigatorio (1 a 15 caracteres)", "seuNumero");
        if (req.ValorNominal is not (>= 2.5m and <= 99999999.99m)) return Erro.Invalid("valorNominal entre 2.50 e 99999999.99", "valorNominal");
        if (!DateOnly.TryParseExact(req.DataVencimento, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var venc))
            return Erro.Invalid("dataVencimento no formato yyyy-MM-dd", "dataVencimento");
        if (venc < Brasilia.DateOf(ctx.Now)) return Erro.Invalid("dataVencimento nao pode estar no passado", "dataVencimento");
        if (req.NumDiasAgenda is < 0 or > 60) return Erro.Invalid("numDiasAgenda entre 0 e 60", "numDiasAgenda");
        if (req.Pagador is not { } p) return Erro.Invalid("pagador obrigatorio", "pagador");
        if (p.CpfCnpj is not { Length: 11 or 14 } doc || !doc.All(char.IsAsciiDigit))
            return Erro.Invalid("pagador.cpfCnpj: 11 ou 14 digitos", "pagador.cpfCnpj");
        if (p.TipoPessoa is not ("FISICA" or "JURIDICA")) return Erro.Invalid("pagador.tipoPessoa: FISICA ou JURIDICA", "pagador.tipoPessoa");
        foreach (var (value, name) in new[] { (p.Nome, "nome"), (p.Endereco, "endereco"), (p.Cidade, "cidade"), (p.Uf, "uf"), (p.Cep, "cep") })
            if (string.IsNullOrWhiteSpace(value)) return Erro.Invalid($"pagador.{name} obrigatorio", $"pagador.{name}");
        return null;
    }


    private static string? Arg(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static InterCobranca? Find(ConnectorContext ctx, string codigo) => ctx.Store.Get<InterCobranca>(codigo);

    private static IResult NotFound(string codigo) => Erro.Of(404, "Cobranca nao encontrada", $"cobranca {codigo} nao encontrada");

    private static string Digits(int n) => string.Concat(RandomNumberGenerator.GetBytes(n).Select(b => (char)('0' + b % 10)));
}

/// Error shape of the Inter APIs outside Pix: title, detail, timestamp and violacoes.
internal static class Erro
{
    public static IResult Of(int status, string title, string detail, object? violacoes = null) =>
        Results.Json(new { title, detail, timestamp = DateTimeOffset.UtcNow, violacoes }, statusCode: status);

    public static IResult Invalid(string detail, string? propriedade = null) =>
        Of(400, "Requisicao invalida", detail, propriedade is null ? null : new[] { new { razao = detail, propriedade } });
}

public sealed record EmitirCobrancaRequest(
    string? SeuNumero = null, decimal? ValorNominal = null, string? DataVencimento = null, int? NumDiasAgenda = null,
    Pagador? Pagador = null, MensagemRequest? Mensagem = null);

public sealed record MensagemRequest(string? Linha1 = null);
public sealed record CancelarCobrancaRequest(string? MotivoCancelamento = null);
public sealed record PagarCobrancaRequest(string? PagarCom = null);
public sealed record ConfigurarWebhookCobrancaRequest(string WebhookUrl);
