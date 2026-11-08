using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Bradesco;

// CONCLUIDO, as the bank answers in production; the cob status stays CONCLUIDA.
public enum SpiStatus { EM_PROCESSAMENTO, CONCLUIDO, REJEITADO }

/// An outgoing Pix through the SPI (POST v1/spi/solicitar-transferencia), keyed by idtransacao.
public sealed class TransferenciaPix : IHasNaturalKey
{
    public string IdTransacao { get; init; } = "";
    public string NaturalKeyProperty => nameof(IdTransacao);

    public SpiStatus Status { get; set; } = SpiStatus.EM_PROCESSAMENTO;
    public decimal Valor { get; init; }
    public string? Descricao { get; init; }
    public string AccountNickname { get; init; } = "";     // payer account (nickname), debited on conclusion
    public SpiRecebedor Recebedor { get; init; } = new();
    public string? EndToEndId { get; set; }
    public DateTimeOffset DataCriacao { get; init; }
    public DateTimeOffset? DataEfetivacao { get; set; }
    public string? Motivo { get; set; }
    public string? CodigoMotivo { get; set; }
}

/// Bradesco SPI (outgoing Pix). The bank publishes no public contract, so the shape follows what
/// client applications are known to send and parse.
internal static partial class BradescoSpiApi
{
    public const string ModeSetting = "SPI_MODE";
    private const string Base = "/bradesco/v1/spi";
    private static readonly string[] KeyTypes = ["EMAIL", "TELEFONE", "CPFCNPJ", "EVP", "AGENCIACONTA"];

    public static readonly Lifecycle<TransferenciaPix, SpiStatus> Transferencia =
        new Lifecycle<TransferenciaPix, SpiStatus>("transferencia-pix", SpiStatus.EM_PROCESSAMENTO, t => t.IdTransacao, t => t.Status, (t, s) => t.Status = s)
            .Allow(SpiStatus.EM_PROCESSAMENTO, SpiStatus.CONCLUIDO, "conclude")
            .Allow(SpiStatus.EM_PROCESSAMENTO, SpiStatus.REJEITADO, "reject");

    public static readonly ConnectorAction[] Actions =
    [
        Transferencia.Action("conclude", "Conclude outgoing Pix", (ctx, t, _) => { Conclude(ctx, t); return Task.FromResult(Results.Ok(ToResource(ctx, t))); }),
        // Rejected by the SPI or the receiving PSP: the Pix went out, so it has an end-to-end id.
        Transferencia.Action("reject", "Reject outgoing Pix", (ctx, t, args) =>
        {
            t.EndToEndId = NewEndToEndId(ctx);
            Reject(ctx, t, Arg(args, "reason") ?? "Pix rejeitado pelo PSP recebedor", Arg(args, "reasonCode"));
            return Task.FromResult(Results.Ok(ToResource(ctx, t)));
        }, new ActionParameter("reason"), new ActionParameter("reasonCode")),
    ];

    public static readonly ConnectorSetting Mode = new(ModeSetting, "SPI outgoing Pix response (sync or async)",
        Help: "sync (default): answers CONCLUIDO with the e2e. async: answers EM_PROCESSAMENTO without e2e until an action concludes or rejects it.");

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost($"{Base}/solicitar-transferencia", (SpiRequest req, HttpContext http, ConnectorContext ctx) =>
            BradescoConnector.Refuse(ctx, http) ?? Request(ctx, http, req));
        routes.MapGet($"{Base}/consulta/transferencia/{{id}}/{{e2e?}}", (string id, string? e2e, HttpContext http, ConnectorContext ctx) =>
        {
            if (BradescoConnector.Refuse(ctx, http) is { } refused) return refused;
            // An empty e2e (still in processing at the request) finds it by id only.
            return ctx.Store.Get<TransferenciaPix>(id) is { } t && (string.IsNullOrEmpty(e2e) || e2e == t.EndToEndId)
                ? Results.Ok(ToResource(ctx, t))
                : Problema.NotFound("TransferenciaNaoEncontrada", $"transferencia {id} nao encontrada");
        });
    }

    private static IResult Request(ConnectorContext ctx, HttpContext http, SpiRequest req)
    {
        if (Violations(req) is { Count: > 0 } violations)
            return Problema.Of(400, "SolicitacaoTransferenciaInvalida", "Requisição inválida.", violations[0].Razao,
                violations.Select(v => new { razao = v.Razao, propriedade = v.Propriedade }).ToList());

        var id = req.IdTransacao!;
        http.Annotate(entityId: id);
        // A repeated idtransacao never pays twice; clients retry a 412 like this one.
        if (ctx.Store.Get<TransferenciaPix>(id) is not null)
            return Problema.Of(412, "TransacaoDuplicada", "Transação em duplicidade.", $"idtransacao {id} ja utilizado");

        var payer = (req.Pagador!.ChavePix is { Length: > 0 } chave
            ? Banking.Accounts(ctx.Store).FirstOrDefault(a => a.Bank == ctx.Connector && a.PixKey == chave)
            : null) ?? BradescoConnector.FindAccount(ctx, req.Pagador.Agencia, req.Pagador.Conta, thisBank: true);
        if (payer is null)
            return Problema.Invalid("SolicitacaoTransferenciaInvalida", "conta do pagador nao encontrada neste sandbox", "solicitacao.pagador.conta");

        var r = req.Recebedor!;
        if (r.TipoChave != "AGENCIACONTA" && Banking.ByPixKey(ctx.Store, r.ChavePix!) is null)
            return Problema.Of(412, "ChaveNaoEncontrada", "Chave Pix não encontrada.", $"chave {r.ChavePix} nao encontrada no DICT");

        var transferencia = new TransferenciaPix
        {
            IdTransacao = id,
            Valor = decimal.Parse(req.Valor!, CultureInfo.InvariantCulture),
            Descricao = req.Descricao,
            AccountNickname = payer.Nickname,
            Recebedor = r,
            DataCriacao = ctx.Now,
        };
        ctx.Create(Transferencia, transferencia);
        if (ctx.Setting(ModeSetting) != PixRefund.Async) Conclude(ctx, transferencia);
        return Results.Ok(ToResource(ctx, transferencia));
    }

    private static List<(string Propriedade, string Razao)> Violations(SpiRequest req)
    {
        var v = new List<(string, string)>();
        if (req.IdTransacao is not { } id || !IdPattern().IsMatch(id)) v.Add(("solicitacao.idtransacao", "26 a 35 alfanumericos"));
        if (req.Valor is not { } valor || !ValorPattern().IsMatch(valor) || decimal.Parse(valor, CultureInfo.InvariantCulture) == 0)
            v.Add(("solicitacao.valor", "string \\d{1,10}.\\d{2} maior que zero"));
        if (req.Pagador is null) v.Add(("solicitacao.pagador", "obrigatorio"));
        if (req.Recebedor is not { } r)
        {
            v.Add(("solicitacao.recebedor", "obrigatorio"));
            return v;
        }
        if (!KeyTypes.Contains(r.TipoChave))
            v.Add(("solicitacao.recebedor.tipoChave", $"tipoChave deve ser um de {string.Join(", ", KeyTypes)}"));
        else if (r.TipoChave == "AGENCIACONTA")
        {
            foreach (var (name, value) in new[] { ("ispb", r.Ispb), ("agencia", r.Agencia), ("conta", r.Conta) })
                if (string.IsNullOrWhiteSpace(value)) v.Add(($"solicitacao.recebedor.{name}", "obrigatorio para AGENCIACONTA"));
        }
        else if (string.IsNullOrWhiteSpace(r.ChavePix)) v.Add(("solicitacao.recebedor.chavePix", "obrigatoria"));
        else if (!Matches(r.TipoChave, r.ChavePix))
            v.Add(("solicitacao.recebedor.tipoChave", $"tipoChave {r.TipoChave} incompativel com a chave informada"));
        // The holder's document is only known here when the key is the document itself.
        else if (r.TipoChave == "CPFCNPJ" && r.CpfCnpj is { Length: > 0 } doc && Digits(doc) != r.ChavePix)
            v.Add(("solicitacao.recebedor.cpfCnpj", "cpfCnpj diferente do titular da chave"));
        return v;
    }

    private static bool Matches(string tipo, string chave) => tipo switch
    {
        "EMAIL" => chave.Length <= 77 && EmailPattern().IsMatch(chave),
        "TELEFONE" => PhonePattern().IsMatch(chave),
        "CPFCNPJ" => DocumentPattern().IsMatch(chave),
        _ => Guid.TryParseExact(chave, "D", out _),
    };

    /// Debits the payer (without balance it is REJEITADO, never sent) and credits a receiver of the sandbox.
    private static void Conclude(ConnectorContext ctx, TransferenciaPix t)
    {
        var details = new Dictionary<string, string?> { ["idTransacao"] = t.IdTransacao, ["descricao"] = t.Descricao };
        if (ctx.Store.Get<BankAccount>(t.AccountNickname) is not { } payer || !Banking.Debit(ctx, payer, t.Valor, "Pix enviado", t.IdTransacao, details))
        {
            Reject(ctx, t, "SALDO INSUFICIENTE", "AM04");
            return;
        }
        t.EndToEndId = NewEndToEndId(ctx);
        details["endToEndId"] = t.EndToEndId;
        var r = t.Recebedor;
        var receiver = r.TipoChave == "AGENCIACONTA"
            ? BradescoConnector.FindAccount(ctx, r.Agencia, $"{r.Conta}{r.DigitoConta}", thisBank: false)
            : Banking.ByPixKey(ctx.Store, r.ChavePix!);
        if (receiver is not null) Banking.Credit(ctx, receiver, t.Valor, "Pix recebido", t.EndToEndId, details);
        t.DataEfetivacao = ctx.Now;
        ctx.Move(Transferencia, t, SpiStatus.CONCLUIDO);
    }

    private static void Reject(ConnectorContext ctx, TransferenciaPix t, string motivo, string? codigoMotivo)
    {
        t.Motivo = motivo;
        t.CodigoMotivo = codigoMotivo;
        ctx.Move(Transferencia, t, SpiStatus.REJEITADO, motivo);
    }

    // E + ISPB + yyyyMMddHHmm (UTC) + 11 alphanumerics, as Bacen formats it.
    private static string NewEndToEndId(ConnectorContext ctx) =>
        $"E60746948{ctx.Now.UtcDateTime:yyyyMMddHHmm}{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))[..11]}";

    private static object ToResource(ConnectorContext ctx, TransferenciaPix t)
    {
        var payer = ctx.Store.Get<BankAccount>(t.AccountNickname);
        var r = t.Recebedor;
        return BradescoConnector.Compact(
            ("idTransacao", t.IdTransacao), ("e2e", t.EndToEndId), ("status", t.Status.ToString()),
            ("dataCriacao", t.DataCriacao), ("dataEfetivacao", t.DataEfetivacao), ("motivo", t.Motivo), ("codigoMotivo", t.CodigoMotivo),
            ("valor", Banking.Money(t.Valor)), ("valorTarifa", "0.00"), ("descricao", t.Descricao),
            ("pagador", BradescoConnector.Compact(("agencia", payer?.Branch), ("conta", payer?.Account), ("tipoConta", "CONTA_CORRENTE"))),
            ("recebedor", BradescoConnector.Compact(("tipoChave", r.TipoChave), ("chavePix", r.ChavePix), ("cpfCnpj", r.CpfCnpj),
                ("nomeFavorecido", r.NomeFavorecido), ("ispb", r.Ispb), ("agencia", r.Agencia), ("conta", r.Conta), ("tipoConta", r.TipoConta))));
    }

    private static string? Arg(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex("^[a-zA-Z0-9]{26,35}$")] private static partial Regex IdPattern();
    [GeneratedRegex(@"^\d{1,10}\.\d{2}$")] private static partial Regex ValorPattern();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailPattern();
    [GeneratedRegex(@"^\+[1-9]\d{1,14}$")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"^(\d{11}|\d{14})$")] private static partial Regex DocumentPattern();
}

public sealed record SpiRequest(string? IdTransacao = null, SpiPagador? Pagador = null, SpiRecebedor? Recebedor = null, string? Valor = null, string? Descricao = null);

public sealed record SpiPagador(string? TipoChave = null, string? ChavePix = null, string? Agencia = null, string? Conta = null);

public sealed record SpiRecebedor(
    string? TipoChave = null, string? ChavePix = null, string? CpfCnpj = null, string? Ispb = null, string? Agencia = null, string? Conta = null,
    string? DigitoConta = null, string? TipoConta = null, string? Banco = null, string? NomeFavorecido = null);
