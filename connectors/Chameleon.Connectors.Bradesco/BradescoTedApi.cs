using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Bradesco;

/// TED states; the API writes EM_PROCESSAMENTO with a space ("EM PROCESSAMENTO").
public enum TedStatus { EM_PROCESSAMENTO, PROCESSADA, DEVOLVIDA }

/// A TED sent from a sandbox account (POST transferencia/ted/v1/efetiva), found by numeroDocumento + dataMovimento.
public sealed class Ted : IHasNaturalKey
{
    public string Chave { get; init; } = "";                    // {numeroDocumento}-{yyyyMMdd}
    public string NaturalKeyProperty => nameof(Chave);

    public int NumeroDocumento { get; init; }
    public string ChaveUnicaParaApi { get; init; } = "";
    public string CodigoIdentificador { get; init; } = "";      // codigoIdentificadorDaTransferencia (idempotency)
    public decimal Valor { get; init; }
    public string AccountNickname { get; init; } = "";          // sender account (nickname), debited on request
    public string AgenciaDestinatario { get; init; } = "";
    public string ContaDestinatario { get; init; } = "";
    public TedStatus Status { get; set; } = TedStatus.EM_PROCESSAMENTO;
    public int? CodigoDaDevolucao { get; set; }
    public string? DescricaoDaDevolucao { get; set; }
    public DateTimeOffset SolicitadaEm { get; init; }
}

/// Bradesco TED: codigoDeRetorno 23
/// is the only success; the query answers 422 for an unknown document.
internal static class BradescoTedApi
{
    public const int Success = 23;
    // Codes from the bank's TED manual (developers.bradesco.com.br, API TED 1.1.6).
    private const int InsufficientBalance = 82, InvalidAccount = 10;
    private const string Base = "/bradesco/transferencia/ted/v1";
    public const string ModeSetting = "TED_MODE";
    public const string Manual = "manual";

    // Automatic by default, like SPI_MODE and REFUND_MODE: clients poll consulta until PROCESSADA,
    // so answering it on the first query only saves a tester a click per TED.
    public static readonly ConnectorSetting Mode = new(ModeSetting, "TED processing (automatic or manual)",
        Help: "automatic (default): the TED becomes PROCESSADA right after the request and credits the destination; the query already answers PROCESSADA. manual: it stays EM PROCESSAMENTO until an action processes or returns it.");

    public static readonly Lifecycle<Ted, TedStatus> Transferencia =
        new Lifecycle<Ted, TedStatus>("ted", TedStatus.EM_PROCESSAMENTO, t => t.Chave, t => t.Status, (t, s) => t.Status = s)
            .Allow(TedStatus.EM_PROCESSAMENTO, TedStatus.PROCESSADA, "process")
            .Allow(TedStatus.EM_PROCESSAMENTO, TedStatus.DEVOLVIDA, "return");

    public static readonly ConnectorAction[] Actions =
    [
        Transferencia.Action("process", "Process TED", (ctx, t, _) => { Process(ctx, t); return Task.FromResult(Results.Ok(Consulta(t))); }),
        // codigoDaDevolucao is a number (the manual says so and clients read it as an int).
        Transferencia.Action("return", "Return TED", (ctx, t, args) =>
        {
            t.CodigoDaDevolucao = int.TryParse(Arg(args, "returnCode"), out var codigo) ? codigo : 2;
            t.DescricaoDaDevolucao = Arg(args, "returnDescription") ?? "AG.OU CTA DEST. INVALIDA";
            if (ctx.Store.Get<BankAccount>(t.AccountNickname) is { } remetente) Banking.Credit(ctx, remetente, t.Valor, "TED devolvida", t.ChaveUnicaParaApi);
            ctx.Move(Transferencia, t, TedStatus.DEVOLVIDA, t.DescricaoDaDevolucao);
            return Task.FromResult(Results.Ok(Consulta(t)));
        }, new ActionParameter("returnCode"), new ActionParameter("returnDescription")),
    ];

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost($"{Base}/efetiva", (TedRequest req, HttpContext http, ConnectorContext ctx) =>
            BradescoConnector.Refuse(ctx, http) ?? Efetiva(ctx, http, req));

        routes.MapGet($"{Base}/consulta", (HttpContext http, ConnectorContext ctx) =>
        {
            if (BradescoConnector.Refuse(ctx, http) is { } refused) return refused;
            var q = http.Request.Query;
            if (!int.TryParse(q["numeroDocumento"], out var numero) || !TryDate(q["dataOperacao"], out var data))
                return Results.BadRequest(new { mensagem = "numeroDocumento (numerico) e dataOperacao (dd.MM.yyyy) obrigatorios" });
            // An unknown document is 422 (clients map it to "004" and poll again).
            return ctx.Store.Get<Ted>(Key(numero, data)) is { } t
                ? Results.Ok(Consulta(t))
                : Results.Json(new { mensagem = "REGISTRO INEXISTENTE" }, statusCode: 422);
        });
    }

    private static IResult Efetiva(ConnectorContext ctx, HttpContext http, TedRequest req)
    {
        if (Invalid(req) is { } motivo) return Results.Json(new { mensagem = motivo }, statusCode: 422);

        // The same codigoIdentificadorDaTransferencia is the same TED.
        if (ctx.Store.All<Ted>().FirstOrDefault(t => t.CodigoIdentificador == req.CodigoIdentificadorDaTransferencia) is { } same)
        {
            http.Annotate(entityId: same.Chave, note: "idempotent hit");
            return Results.Ok(Efetivada(same));
        }

        if (BradescoConnector.FindAccount(ctx, req.AgenciaRemetente.ToString(), req.ContaRemetenteComDigito.ToString(), thisBank: true) is not { } remetente)
            return Results.Ok(new { codigoDeRetorno = InvalidAccount, mensagem = "AGENCIA/CONTA NAO CADASTRADA" });

        TryDate(req.DataMovimento, out var data);
        int numero;
        do numero = RandomNumberGenerator.GetInt32(1_000_000, 10_000_000);
        while (ctx.Store.Get<Ted>(Key(numero, data)) is not null);
        var ted = new Ted
        {
            Chave = Key(numero, data),
            NumeroDocumento = numero,
            ChaveUnicaParaApi = $"{numero}{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))[..5]}",
            CodigoIdentificador = req.CodigoIdentificadorDaTransferencia!,
            Valor = Math.Round(req.ValorDaTransferencia!.Value, 2),
            AccountNickname = remetente.Nickname,
            AgenciaDestinatario = req.AgenciaDestinatario!.Value.ToString(CultureInfo.InvariantCulture),
            ContaDestinatario = req.ContaDestinatario!.Value.ToString(CultureInfo.InvariantCulture),
            SolicitadaEm = ctx.Now,
        };
        if (!Banking.Debit(ctx, remetente, ted.Valor, "TED enviada", ted.ChaveUnicaParaApi, new() { ["codigoIdentificador"] = ted.CodigoIdentificador }))
            return Results.Ok(new { codigoDeRetorno = InsufficientBalance, mensagem = "SALDO INSUFICIENTE" });
        http.Annotate(entityId: ted.Chave);
        ctx.Create(Transferencia, ted);
        if (ctx.Setting(ModeSetting) != Manual) Process(ctx, ted);
        return Results.Ok(Efetivada(ted));
    }

    /// Credits the destination when its account is in the sandbox.
    private static void Process(ConnectorContext ctx, Ted t)
    {
        if (BradescoConnector.FindAccount(ctx, t.AgenciaDestinatario, t.ContaDestinatario, thisBank: false) is { } destino)
            Banking.Credit(ctx, destino, t.Valor, "TED recebida", t.ChaveUnicaParaApi);
        ctx.Move(Transferencia, t, TedStatus.PROCESSADA);
    }

    private static string? Invalid(TedRequest req)
    {
        if (req.AgenciaRemetente is not > 0 || req.ContaRemetenteComDigito is not > 0) return "agenciaRemetente e contaRemetenteComDigito obrigatorios";
        if (req.BancoDestinatario is not > 0 || req.AgenciaDestinatario is not (> 0 and <= 9999) || req.ContaDestinatario is not > 0)
            return "bancoDestinatario, agenciaDestinatario (ate 4 digitos) e contaDestinatario obrigatorios";
        if (req.ValorDaTransferencia is not > 0) return "valorDaTransferencia deve ser maior que zero";
        if (!TryDate(req.DataMovimento, out _)) return "dataMovimento obrigatoria no formato dd.MM.yyyy";
        if (req.CodigoIdentificadorDaTransferencia is not { Length: > 0 and <= 25 }) return "codigoIdentificadorDaTransferencia: 1 a 25 caracteres";
        if (req.NumeroFilial is { Length: > 5 }) return "numeroFilial: tamanho maximo de 5 caracteres excedido";
        return null;
    }

    // codigoDeRetorno is a number in the efetiva response.
    private static object Efetivada(Ted t) =>
        new { codigoDeRetorno = Success, mensagem = "OPERACAO EFETUADA COM SUCESSO", chaveUnicaParaApi = t.ChaveUnicaParaApi };

    // The query names it codigoRetorno, a string.
    private static Dictionary<string, object?> Consulta(Ted t) => BradescoConnector.Compact(
        ("codigoRetorno", "000"), ("mensagem", "CONSULTA EFETUADA COM SUCESSO"),
        ("statusMensagem", t.Status == TedStatus.EM_PROCESSAMENTO ? "EM PROCESSAMENTO" : t.Status.ToString()),
        ("codigoDaDevolucao", t.CodigoDaDevolucao), ("descricaoDaDevolucao", t.DescricaoDaDevolucao));

    private static string Key(int numero, DateOnly data) => $"{numero}-{data:yyyyMMdd}";

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string? Arg(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.String or JsonValueKind.Number && v.ToString() is { Length: > 0 } s ? s : null;
}

/// The TED body; numbers as clients send them.
public sealed record TedRequest(
    int? IdentificadorDoTipoDeTransferencia = null, int? AgenciaRemetente = null, long? ContaRemetenteComDigito = null, string? TipoContaRemetente = null,
    string? TipoDePessoaRemetente = null, string? NomeClienteRemetente = null, int? BancoDestinatario = null, int? AgenciaDestinatario = null,
    long? ContaDestinatario = null, string? TipoDeContaDestinatario = null, string? TipodePessoaDestinatario = null, string? NomeClienteDestinatario = null,
    string? NumeroInscricao = null, string? NumeroFilial = null, string? NumeroControle = null, decimal? ValorDaTransferencia = null,
    int? FinalidadeDaTransferencia = null, string? TipoDeDoc = null, string? DataMovimento = null, string? CodigoIdentificadorDaTransferencia = null,
    int? CanalPagamento = null, string? IndicadorDda = null);
