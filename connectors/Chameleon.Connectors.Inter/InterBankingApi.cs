using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Inter;

public enum TipoPagamento { PIX, BOLETO }

/// Outgoing operation state; the API shows it in each product's vocabulary (see Status).
public enum SituacaoPagamento { APROVADO, REALIZADO, FALHA, CANCELADO }

/// An outgoing Pix or boleto payment from a sandbox account, settled asynchronously.
public sealed class InterPagamento : IHasNaturalKey, IHasDueAt
{
    public string Codigo { get; init; } = "";   // codigoSolicitacao (Pix) or codigoTransacao (boleto)
    public string NaturalKeyProperty => nameof(Codigo);

    public TipoPagamento Tipo { get; init; }
    public decimal Valor { get; init; }
    public string AccountNickname { get; init; } = "";  // sandbox account nickname debited on settlement
    public SituacaoPagamento Situacao { get; set; } = SituacaoPagamento.APROVADO;
    public DateTimeOffset SolicitadoEm { get; init; }
    public DateTimeOffset LiquidaEm { get; init; }
    public DateTimeOffset? MovimentadoEm { get; set; }
    public string? Motivo { get; set; }
    public string? IdIdempotente { get; init; }
    public string? Descricao { get; init; }

    // Pix destination (one of): key, bank details or copy-and-paste code.
    public string? Chave { get; init; }
    public string? PixCopiaECola { get; init; }
    public string? DestinatarioNome { get; init; }
    public string? DestinatarioCpfCnpj { get; init; }
    public string? EndToEndId { get; set; }

    // Boleto.
    public string? CodigoBarras { get; init; }
    public string? DataVencimento { get; init; }

    public List<EventoPagamento> Historico { get; init; } = [];

    public DateTimeOffset? DueAt => Situacao == SituacaoPagamento.APROVADO ? LiquidaEm : null;

    /// The state as the Inter names it for this product.
    public string Status => (Tipo, Situacao) switch
    {
        (TipoPagamento.PIX, SituacaoPagamento.APROVADO) => "APROVACAO",
        (TipoPagamento.PIX, SituacaoPagamento.REALIZADO) => "EFETIVADO",
        (TipoPagamento.PIX, SituacaoPagamento.FALHA) => "REJEITADO",
        _ => Situacao.ToString(),
    };
}

public sealed record EventoPagamento(string Status, DateTimeOffset DataHoraEvento, string Descricao);

/// Inter Banking v2: balance and statement from the sandbox ledger, outgoing Pix and boleto payment
/// settled after the connector's deadline, the operator's settle/refuse actions and a Hold on settlement.
internal static class InterBankingApi
{
    public const string ExtratoRead = "extrato.read";
    public const string PixWrite = "pagamento-pix.write", PixRead = "pagamento-pix.read";
    public const string BoletoWrite = "pagamento-boleto.write", BoletoRead = "pagamento-boleto.read";
    public const string WebhookWrite = "webhook-banking.write", WebhookRead = "webhook-banking.read";
    public const string PixWebhook = "pix-pagamento", BoletoWebhook = "boleto-pagamento";
    public const string DeadlineSetting = "SETTLEMENT_DELAY_SECONDS";
    public const string PixReturnSetting = "PIX_RESPONSE";
    private const int DefaultDeadlineSeconds = 5;
    private const string Base = "/inter/banking/v2";
    private const string IdempotencyHeader = "x-id-idempotente";

    public static readonly Lifecycle<InterPagamento, SituacaoPagamento> Pagamento =
        new Lifecycle<InterPagamento, SituacaoPagamento>("pagamento", SituacaoPagamento.APROVADO,
                p => p.Codigo, p => p.Situacao, (p, s) => p.Situacao = s)
            .Allow(SituacaoPagamento.APROVADO, SituacaoPagamento.REALIZADO, "settle")
            .Allow(SituacaoPagamento.APROVADO, SituacaoPagamento.FALHA, "reject")
            .Allow(SituacaoPagamento.APROVADO, SituacaoPagamento.CANCELADO, "cancel");

    public static readonly ConnectorAction[] Actions =
    [
        // Explicit operator action: settles even with a Hold armed on settlement.
        Pagamento.Action("settle", "Settle now", async (ctx, p, _) => { await SettleAsync(ctx, p); return Results.Ok(ToResource(p)); }),
        Pagamento.Action("reject", "Reject", async (ctx, p, args) =>
        {
            await FailAsync(ctx, p, Arg(args, "reason") ?? "recusado pelo simulador");
            return Results.Ok(ToResource(p));
        }, new ActionParameter("reason")),
    ];

    public static readonly ConnectorSetting Deadline = new(DeadlineSetting, "Settlement delay of outgoing operations (seconds)", "number",
        Help: $"Outgoing Pix and boleto payments settle after this delay on the sandbox clock. Default {DefaultDeadlineSeconds}.");

    public static readonly ConnectorSetting PixReturn = new(PixReturnSetting, "Outgoing Pix response (APROVACAO or PROCESSADO)",
        Help: "APROVACAO (default): answers without endToEndId and settles after the delay, with a webhook. PROCESSADO: settles right away and already answers the endToEndId.");

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet($"{Base}/saldo", (HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, ExtratoRead, out var account) is { } refused) return refused;
            return Results.Ok(new { disponivel = account!.Balance, bloqueadoCheque = 0m, bloqueadoJudicialmente = 0m, bloqueadoAdministrativo = 0m, limite = 0m });
        });

        routes.MapGet($"{Base}/extrato", (HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, ExtratoRead, out var account) is { } refused) return refused;
            if (Period(http.Request.Query) is not var (inicio, fim)) return Erro.Invalid("dataInicio e dataFim obrigatorias (yyyy-MM-dd)", "dataInicio");
            return Results.Ok(new
            {
                transacoes = Entries(account!, inicio, fim).Select(e => new
                {
                    dataEntrada = Brasilia.DateOf(e.At).ToString("yyyy-MM-dd"),
                    tipoTransacao = TipoTransacao(e),
                    tipoOperacao = e.Amount >= 0 ? "C" : "D",
                    valor = Banking.Money(Math.Abs(e.Amount)),
                    titulo = e.Description,
                    descricao = e.Reference is null ? e.Description : $"{e.Description} {e.Reference}",
                }),
            });
        });

        routes.MapGet($"{Base}/extrato/completo", (HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, ExtratoRead, out var account) is { } refused) return refused;
            var q = http.Request.Query;
            if (Period(q) is not var (inicio, fim)) return Erro.Invalid("dataInicio e dataFim obrigatorias (yyyy-MM-dd)", "dataInicio");
            var all = Entries(account!, inicio, fim).ToList();
            var page = int.TryParse(q["pagina"], out var p) ? Math.Max(p, 0) : 0;
            var size = int.TryParse(q["tamanhoPagina"], out var s) ? Math.Clamp(s, 1, 10000) : 50;
            var items = all.Skip(page * size).Take(size).Select(e => new
            {
                idTransacao = e.Id,
                dataInclusao = e.At,
                dataTransacao = Brasilia.DateOf(e.At).ToString("yyyy-MM-dd"),
                tipoTransacao = TipoTransacao(e),
                tipoOperacao = e.Amount >= 0 ? "C" : "D",
                valor = Banking.Money(Math.Abs(e.Amount)),
                titulo = e.Description,
                descricao = e.Reference,
                detalhes = e.Details ?? [],
            }).ToList();
            var pages = (all.Count + size - 1) / size;
            return Results.Ok(new
            {
                totalPaginas = pages, totalElementos = all.Count, ultimaPagina = page >= pages - 1, primeiraPagina = page == 0,
                tamanhoPagina = size, numeroDeElementos = items.Count, transacoes = items,
            });
        });

        routes.MapPost($"{Base}/pix", async (PixSaidaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, PixWrite, out var account) is { } refused) return refused;
            var idempotente = http.Request.Headers[IdempotencyHeader].ToString() is { Length: > 0 } h ? h : null;
            // The Inter refuses a repeated idempotency key with 422; the caller finds the first one in the statement.
            if (idempotente is not null && ctx.Store.All<InterPagamento>().FirstOrDefault(p => p.IdIdempotente == idempotente) is { } same)
            {
                http.Annotate(note: "idempotent hit");
                return Erro.Of(422, "Requisicao duplicada", $"x-id-idempotente ja utilizado (codigoSolicitacao {same.Codigo})");
            }
            if (InvalidPix(req) is { } invalid) return invalid;
            if (account!.Balance < req.Valor!.Value) return Insufficient(account);

            var d = req.Destinatario!;
            var pix = new InterPagamento
            {
                Codigo = Guid.NewGuid().ToString(),
                Tipo = TipoPagamento.PIX,
                Valor = Math.Round(req.Valor.Value, 2),
                AccountNickname = account.Nickname,
                SolicitadoEm = ctx.Now,
                LiquidaEm = ctx.Now.AddSeconds(DeadlineSeconds(ctx)),
                IdIdempotente = idempotente,
                Descricao = req.Descricao,
                Chave = d.Tipo == "CHAVE" ? d.Chave : null,
                PixCopiaECola = d.Tipo == "PIX_COPIA_E_COLA" ? d.PixCopiaECola : null,
                DestinatarioNome = d.Nome,
                DestinatarioCpfCnpj = d.CpfCnpj,
            };
            pix.Historico.Add(new("APROVACAO", ctx.Now, "Pix solicitado"));
            ctx.Create(Pagamento, pix);
            if (ctx.Setting(PixReturnSetting) == "PROCESSADO") await SettleAsync(ctx, pix);
            return Results.Ok(PixCreated(pix));
        });

        routes.MapGet($"{Base}/pix/{{codigo}}", (string codigo, HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, PixRead, out _) is { } refused) return refused;
            if (ctx.Store.Get<InterPagamento>(codigo) is not { Tipo: TipoPagamento.PIX } p) return Erro.Of(404, "Pix nao encontrado", $"pix {codigo} nao encontrado");
            return Results.Ok(new
            {
                transacaoPix = new
                {
                    contaCorrente = ctx.Store.Get<BankAccount>(p.AccountNickname)?.Account,
                    recebedor = new { nome = p.DestinatarioNome, cpfCnpj = p.DestinatarioCpfCnpj, chave = p.Chave },
                    erros = p.Situacao == SituacaoPagamento.FALHA ? new[] { new { codigoErro = "PIX_NAO_REALIZADO", descricaoErro = p.Motivo } } : [],
                    endToEndId = p.EndToEndId,
                    valor = Banking.Money(p.Valor),
                    status = p.Status,
                    dataHoraMovimento = p.MovimentadoEm,
                    dataHoraSolicitacao = p.SolicitadoEm,
                    chave = p.Chave,
                    codigoSolicitacao = p.Codigo,
                },
                historico = p.Historico,
            });
        });

        routes.MapPost($"{Base}/pagamento", (PagarBoletoRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, BoletoWrite, out var account) is { } refused) return refused;
            if (req.CodBarraLinhaDigitavel is not { Length: 44 or 47 } code || !code.All(char.IsAsciiDigit))
                return Erro.Invalid("codBarraLinhaDigitavel: 44 ou 47 digitos", "codBarraLinhaDigitavel");
            if (req.ValorPagar is not > 0) return Erro.Invalid("valorPagar deve ser maior que zero", "valorPagar");
            if (!DateOnly.TryParseExact(req.DataVencimento, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Erro.Invalid("dataVencimento no formato yyyy-MM-dd", "dataVencimento");
            // The same boleto still pending: the same payment.
            if (ctx.Store.All<InterPagamento>().FirstOrDefault(p => p.Tipo == TipoPagamento.BOLETO && p.CodigoBarras == code
                    && p.Situacao == SituacaoPagamento.APROVADO && p.AccountNickname == account!.Nickname) is { } same)
            {
                http.Annotate(note: "idempotent hit");
                return Results.Ok(BoletoCreated(same));
            }
            if (account!.Balance < req.ValorPagar.Value) return Insufficient(account);

            var pagamento = new InterPagamento
            {
                Codigo = Guid.NewGuid().ToString(),
                Tipo = TipoPagamento.BOLETO,
                Valor = Math.Round(req.ValorPagar.Value, 2),
                AccountNickname = account.Nickname,
                SolicitadoEm = ctx.Now,
                LiquidaEm = ctx.Now.AddSeconds(DeadlineSeconds(ctx)),
                CodigoBarras = code,
                DataVencimento = req.DataVencimento,
                DestinatarioCpfCnpj = req.CpfCnpjBeneficiario,
            };
            pagamento.Historico.Add(new("APROVADO", ctx.Now, "Pagamento solicitado"));
            ctx.Create(Pagamento, pagamento);
            return Results.Ok(BoletoCreated(pagamento));
        });

        routes.MapGet($"{Base}/pagamento", (HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, BoletoRead, out var account) is { } refused) return refused;
            var q = http.Request.Query;
            if (Period(q) is not var (inicio, fim)) return Erro.Invalid("dataInicio e dataFim obrigatorias (yyyy-MM-dd)", "dataInicio");
            return Results.Ok(ctx.Store.All<InterPagamento>()
                .Where(p => p.Tipo == TipoPagamento.BOLETO && p.AccountNickname == account!.Nickname)
                .Where(p => Brasilia.DateOf(p.SolicitadoEm) >= inicio && Brasilia.DateOf(p.SolicitadoEm) <= fim)
                .Where(p => q["codigoTransacao"].ToString() is not { Length: > 0 } c || p.Codigo == c)
                .OrderBy(p => p.SolicitadoEm)
                .Select(p => new
                {
                    codigoTransacao = p.Codigo, codigoBarra = p.CodigoBarras, tipo = "BOLETO", dataVencimento = p.DataVencimento,
                    dataPagamento = p.MovimentadoEm is { } m ? Brasilia.DateOf(m).ToString("yyyy-MM-dd") : null,
                    dataInclusao = p.SolicitadoEm, valorPago = Banking.Money(p.Valor), statusPagamento = p.Status, motivo = p.Motivo,
                }));
        });

        routes.MapDelete($"{Base}/pagamento/{{codigo}}", async (string codigo, HttpContext http, ConnectorContext ctx) =>
        {
            if (Refuse(ctx, http, BoletoWrite, out _) is { } refused) return refused;
            if (ctx.Store.Get<InterPagamento>(codigo) is not { Tipo: TipoPagamento.BOLETO } p)
                return Erro.Of(404, "Pagamento nao encontrado", $"pagamento {codigo} nao encontrado");
            if (p.Situacao != SituacaoPagamento.APROVADO) return Erro.Invalid($"pagamento {p.Status} nao pode ser cancelado");
            await MoveAsync(ctx, p, SituacaoPagamento.CANCELADO, "Pagamento cancelado");
            return Results.NoContent();
        });

        routes.MapPut($"{Base}/webhooks/{{tipo}}", (string tipo, ConfigurarWebhookCobrancaRequest req, HttpContext http, ConnectorContext ctx) =>
        {
            if (SimulatedOAuth.Refuse(ctx, http, WebhookWrite) is { } refused) return refused;
            if (tipo is not (PixWebhook or BoletoWebhook)) return Erro.Of(404, "Webhook nao encontrado", $"tipo de webhook desconhecido: {tipo}");
            if (!WebhookRegistrations.IsValidUrl(req.WebhookUrl) || !req.WebhookUrl.StartsWith("https://", StringComparison.Ordinal))
                return Erro.Invalid("webhookUrl deve ser https", "webhookUrl");
            InterConnector.SetAccountWebhook(ctx, tipo, req.WebhookUrl);
            return Results.NoContent();
        });
        routes.MapGet($"{Base}/webhooks/{{tipo}}", (string tipo, HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, WebhookRead) ?? (tipo is PixWebhook or BoletoWebhook && InterConnector.AccountWebhook(ctx, tipo) is { } w
                ? Results.Ok(new { webhookUrl = w.WebhookUrl, criacao = w.Criacao })
                : Erro.Of(404, "Webhook nao encontrado", $"nenhum webhook {tipo} cadastrado")));
        routes.MapDelete($"{Base}/webhooks/{{tipo}}", (string tipo, HttpContext http, ConnectorContext ctx) =>
            SimulatedOAuth.Refuse(ctx, http, WebhookWrite) ?? (tipo is PixWebhook or BoletoWebhook && InterConnector.UnsetAccountWebhook(ctx, tipo)
                ? Results.NoContent()
                : Erro.Of(404, "Webhook nao encontrado", $"nenhum webhook {tipo} cadastrado")));
    }

    /// Settles what is due, unless the operator holds settlement.
    public static async Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        if (ctx.IsHeld(IFaultStore.SettlementRoute)) return;
        foreach (var p in ctx.Due<InterPagamento>().Where(p => p.Situacao == SituacaoPagamento.APROVADO).OrderBy(p => p.SolicitadoEm))
            await SettleAsync(ctx, p);
    }

    public static object Sample(string tipo, DateTimeOffset now) => tipo == PixWebhook
        ? new[] { new { codigoSolicitacao = "00000000-0000-0000-0000-000000000000", chave = "teste", dataHoraMovimento = now, dataHoraSolicitacao = now,
            endToEnd = "E00000000TESTE", recebedor = new { cpfCnpj = "***", nome = "TESTE" }, status = "EFETIVADO", tipoMovimentacao = "PAGAMENTO", valor = "0.01", motivo = (string?)null } }
        : new[] { new { codigoTransacao = "00000000-0000-0000-0000-000000000000", status = "REALIZADO", codigoBarra = new string('0', 44), valorPago = "0.01", dataHoraMovimento = now, motivo = (string?)null } };

    /// Debits the account (re-checking the balance) and delivers to a destination inside the sandbox.
    private static async Task SettleAsync(ConnectorContext ctx, InterPagamento p)
    {
        var endToEnd = p.Tipo == TipoPagamento.PIX ? "E" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16))[..31] : null;
        Dictionary<string, string?> details = p.Tipo == TipoPagamento.PIX
            ? new() { ["endToEndId"] = endToEnd, ["descricaoPix"] = p.Descricao, ["chavePixRecebedor"] = p.Chave, ["codigoSolicitacao"] = p.Codigo }
            : new() { ["codigoTransacao"] = p.Codigo, ["codigoBarras"] = p.CodigoBarras };
        if (ctx.Store.Get<BankAccount>(p.AccountNickname) is not { } account
            || !Banking.Debit(ctx, account, p.Valor, p.Tipo == TipoPagamento.PIX ? "Pix enviado" : "Pagamento de boleto", p.Codigo, details))
        {
            await FailAsync(ctx, p, "SALDO_INSUFICIENTE");
            return;
        }

        if (p.Tipo == TipoPagamento.PIX)
        {
            p.EndToEndId = endToEnd;
            if (p.Chave is not null && Banking.ByPixKey(ctx.Store, p.Chave) is { } owner)
                Banking.Credit(ctx, owner, p.Valor, "Pix recebido", p.EndToEndId, new() { ["endToEndId"] = p.EndToEndId });
            else if (p.PixCopiaECola is not null)
                await PayCopiaECola(ctx, p.PixCopiaECola);
        }
        else if (ctx.Store.All<InterCobranca>().FirstOrDefault(c => c.CodigoBarras == p.CodigoBarras || c.LinhaDigitavel == p.CodigoBarras) is { } boleto
                 && InterCobrancaApi.Cobranca.CanMove(boleto.Situacao, SituacaoCobranca.RECEBIDO))
        {
            await InterCobrancaApi.ReceiveAsync(ctx, boleto, "BOLETO");
        }

        await MoveAsync(ctx, p, SituacaoPagamento.REALIZADO, p.Tipo == TipoPagamento.PIX ? "Pix efetivado" : "Pagamento realizado");
    }

    /// A copy-and-paste code of an Inter charge in this sandbox pays it (boleto with Pix, or cob).
    private static async Task PayCopiaECola(ConnectorContext ctx, string code)
    {
        if (ctx.Store.All<InterCobranca>().FirstOrDefault(c => c.PixCopiaECola == code) is { } boleto)
        {
            if (InterCobrancaApi.Cobranca.CanMove(boleto.Situacao, SituacaoCobranca.RECEBIDO)) await InterCobrancaApi.ReceiveAsync(ctx, boleto, "PIX");
            return;
        }
        if (ctx.Store.All<InterPixCob>().FirstOrDefault(c => c.PixCopiaECola == code) is { Status: CobrancaStatus.ATIVA } cob)
            await InterConnector.Pix.Pay.Run(ctx, cob.TxId, null);
    }

    private static async Task FailAsync(ConnectorContext ctx, InterPagamento p, string motivo)
    {
        p.Motivo = motivo;
        await MoveAsync(ctx, p, SituacaoPagamento.FALHA, $"Nao realizado: {motivo}");
    }

    private static async Task MoveAsync(ConnectorContext ctx, InterPagamento p, SituacaoPagamento to, string descricao)
    {
        ctx.Move(Pagamento, p, to);
        p.MovimentadoEm = ctx.Now;
        p.Historico.Add(new(p.Status, ctx.Now, descricao));
        ctx.Save(p.Codigo, p);
        var tipo = p.Tipo == TipoPagamento.PIX ? PixWebhook : BoletoWebhook;
        object callback = p.Tipo == TipoPagamento.PIX
            ? new[] { new
            {
                codigoSolicitacao = p.Codigo, chave = p.Chave, dataHoraMovimento = p.MovimentadoEm, dataHoraSolicitacao = p.SolicitadoEm,
                endToEnd = p.EndToEndId, recebedor = new { cpfCnpj = p.DestinatarioCpfCnpj, nome = p.DestinatarioNome },
                status = p.Status, tipoMovimentacao = "PAGAMENTO", valor = Banking.Money(p.Valor), motivo = p.Motivo,
            } }
            : new[] { new { codigoTransacao = p.Codigo, status = p.Status, codigoBarra = p.CodigoBarras, valorPago = Banking.Money(p.Valor), dataHoraMovimento = p.MovimentadoEm, motivo = p.Motivo } };
        await ctx.NotifyAsync(p.Codigo, InterConnector.AccountWebhook(ctx, tipo)?.WebhookUrl, callback);
    }

    /// Scope, then the account (required in banking: 400 without one, 403 when unknown).
    private static IResult? Refuse(ConnectorContext ctx, HttpContext http, string scope, out BankAccount? account)
    {
        account = null;
        if (SimulatedOAuth.Refuse(ctx, http, scope) is { } refused) return refused;
        var (conta, error) = InterConnector.AccountOf(ctx, http);
        if (error is not null) return error;
        account = conta is null ? null : ctx.Store.Get<BankAccount>(conta);
        return account is null ? Erro.Of(400, "Conta corrente", "nenhuma conta inter cadastrada neste sandbox") : null;
    }

    private static IResult? InvalidPix(PixSaidaRequest req)
    {
        if (req.Valor is not > 0) return Erro.Invalid("valor deve ser maior que zero", "valor");
        if (req.Destinatario is not { } d) return Erro.Invalid("destinatario obrigatorio", "destinatario");
        return d.Tipo switch
        {
            "CHAVE" when string.IsNullOrWhiteSpace(d.Chave) || d.Chave.Length > 77 => Erro.Invalid("destinatario.chave obrigatoria", "destinatario.chave"),
            "PIX_COPIA_E_COLA" when string.IsNullOrWhiteSpace(d.PixCopiaECola) => Erro.Invalid("destinatario.pixCopiaECola obrigatorio", "destinatario.pixCopiaECola"),
            "DADOS_BANCARIOS" when new[] { d.Nome, d.CpfCnpj, d.ContaCorrente, d.Agencia, d.InstituicaoFinanceira?.Ispb }.Any(string.IsNullOrWhiteSpace) =>
                Erro.Invalid("destinatario: nome, cpfCnpj, contaCorrente, agencia e instituicaoFinanceira.ispb obrigatorios", "destinatario"),
            "CHAVE" or "PIX_COPIA_E_COLA" or "DADOS_BANCARIOS" => null,
            _ => Erro.Invalid("destinatario.tipo: CHAVE, DADOS_BANCARIOS ou PIX_COPIA_E_COLA", "destinatario.tipo"),
        };
    }

    private static IResult Insufficient(BankAccount account) =>
        Erro.Of(422, "Saldo insuficiente", $"saldo disponivel {Banking.Money(account.Balance)} na conta {account.Account}");

    private static object PixCreated(InterPagamento p) => new
    {
        tipoRetorno = p.Situacao == SituacaoPagamento.REALIZADO ? "PROCESSADO" : "APROVACAO", codigoSolicitacao = p.Codigo, endToEndId = p.EndToEndId,
        dataPagamento = Brasilia.DateOf(p.SolicitadoEm).ToString("yyyy-MM-dd"), dataOperacao = Brasilia.DateOf(p.SolicitadoEm).ToString("yyyy-MM-dd"),
    };

    private static object BoletoCreated(InterPagamento p) => new
    {
        quantidadeAprovadores = 0, dataAgendamento = Brasilia.DateOf(p.SolicitadoEm).ToString("yyyy-MM-dd"), statusPagamento = p.Status, codigoTransacao = p.Codigo,
    };

    private static object ToResource(InterPagamento p) => new
    {
        codigo = p.Codigo, tipo = p.Tipo.ToString(), status = p.Status, valor = Banking.Money(p.Valor), conta = p.AccountNickname,
        liquidaEm = p.LiquidaEm, motivo = p.Motivo, endToEndId = p.EndToEndId, historico = p.Historico,
    };

    private static int DeadlineSeconds(ConnectorContext ctx) =>
        int.TryParse(ctx.Setting(DeadlineSetting), out var s) && s >= 0 ? s : DefaultDeadlineSeconds;

    private static IEnumerable<LedgerEntry> Entries(BankAccount account, DateOnly inicio, DateOnly fim) =>
        account.Entries.Where(e => Brasilia.DateOf(e.At) >= inicio && Brasilia.DateOf(e.At) <= fim).OrderBy(e => e.At);

    private static string TipoTransacao(LedgerEntry e) => e.Description switch
    {
        _ when e.Kind == LedgerKind.Adjustment => "OUTROS",
        "Boleto recebido" => "BOLETO_COBRANCA",
        "Pagamento de boleto" => "PAGAMENTO",
        _ when e.Description.Contains("Pix") => "PIX",
        _ => "OUTROS",
    };

    private static (DateOnly, DateOnly)? Period(IQueryCollection q) =>
        DateOnly.TryParseExact(q["dataInicio"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var i)
        && DateOnly.TryParseExact(q["dataFim"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            ? (i, f) : null;


    private static string? Arg(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed record PixSaidaRequest(decimal? Valor = null, string? Descricao = null, string? DataPagamento = null, DestinatarioRequest? Destinatario = null);

public sealed record DestinatarioRequest(
    string? Tipo = null, string? Chave = null, string? PixCopiaECola = null,
    string? Nome = null, string? CpfCnpj = null, string? ContaCorrente = null, string? Agencia = null, string? TipoConta = null,
    InstituicaoRequest? InstituicaoFinanceira = null);

public sealed record InstituicaoRequest(string? Ispb = null);

public sealed record PagarBoletoRequest(
    string? CodBarraLinhaDigitavel = null, decimal? ValorPagar = null, string? DataPagamento = null, string? DataVencimento = null,
    string? CpfCnpjBeneficiario = null);
