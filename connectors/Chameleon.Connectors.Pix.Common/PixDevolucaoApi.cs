using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;

namespace Chameleon.Connectors.Pix.Common;

/// How the PSP answers a refund: at once (DEVOLVIDO in the response) or in processing until the
/// operator settles or refuses it.
public static class PixRefund
{
    public const string ModeSetting = "REFUND_MODE";
    public const string Async = "async";

    public static readonly ConnectorSetting Mode = new(ModeSetting, "Pix refund response (sync or async)",
        Help: "sync (default): answers DEVOLVIDO and debits right away. async: answers EM_PROCESSAMENTO until an action settles or refuses it.");
}

/// Bacen devolucao: refunds of a received Pix, at most its paid amount in total, debiting the account
/// that owns the key and notifying devolucoes[] in the key's webhook.
public sealed partial class PixCobApi<TCob, TWebhook, TDevolucao>
{
    public Lifecycle<TDevolucao, DevolucaoStatus> Devolucao { get; }
    public ConnectorAction[] RefundActions { get; }

    public EntityDescriptor DescribeRefund() => Devolucao.Describe("chave");

    private void MapDevolucao(IEndpointRouteBuilder routes, Func<ConnectorContext, HttpContext, string, IResult?> authorize)
    {
        var b = _o.BasePath;
        routes.MapPut($"{b}/pix/{{e2eid}}/devolucao/{{id}}", async (string e2eid, string id, DevolucaoRequest req, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, PixWrite) ?? await RefundAsync(ctx, http, e2eid, id, req));
        routes.MapGet($"{b}/pix/{{e2eid}}/devolucao/{{id}}", (string e2eid, string id, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, PixRead) ?? (ctx.Store.Get<TDevolucao>(RefundKey(e2eid, id)) is { } d
                ? Results.Ok(ToRefundResource(d))
                : Problema.NotFound("DevolucaoNaoEncontrada", $"devolucao {id} do pix {e2eid} nao encontrada")));
    }

    private async Task<IResult> RefundAsync(ConnectorContext ctx, HttpContext http, string e2eid, string id, DevolucaoRequest req)
    {
        if (!RefundIdPattern().IsMatch(id)) return Problema.Invalid("DevolucaoOperacaoInvalida", "id: 1 a 35 alfanumericos", "id");
        if (ctx.Store.All<TCob>().FirstOrDefault(c => c.EndToEndId == e2eid) is not { } cob)
            return Problema.NotFound("PixNaoEncontrado", $"pix {e2eid} nao encontrado");
        var key = RefundKey(e2eid, id);
        http.Annotate(entityId: key);
        if (ctx.Store.Get<TDevolucao>(key) is not null)
            return Problema.Of(412, "DevolucaoIdJaUtilizado", "Id de devolução já utilizado.", $"a devolucao {id} ja existe para o pix {e2eid}");
        if (req.Valor is not { } v || !ValorPattern().IsMatch(v) || decimal.Parse(v, CultureInfo.InvariantCulture) == 0)
            return Problema.Invalid("DevolucaoOperacaoInvalida", "valor deve ser \\d{1,10}.\\d{2} e maior que zero", "valor");

        var valor = decimal.Parse(v, CultureInfo.InvariantCulture);
        var refundable = (cob.ValorPago ?? cob.ValorOriginal) - Refunds(ctx, e2eid).Where(d => d.Status != DevolucaoStatus.NAO_REALIZADO).Sum(d => d.Valor);
        if (valor > refundable)
            return Problema.Invalid("DevolucaoOperacaoInvalida", $"valor excede o saldo devolvivel do pix ({Banking.Money(refundable)})", "valor");

        var devolucao = new TDevolucao
        {
            Chave = key, EndToEndId = e2eid, Id = id, TxId = cob.TxId, Valor = valor, Solicitacao = ctx.Now,
            RtrId = $"D{_o.Ispb}{ctx.Now.UtcDateTime:yyyyMMddHHmm}{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))[..11]}",
        };
        ctx.Create(Devolucao, devolucao);
        if (ctx.Setting(PixRefund.ModeSetting) != PixRefund.Async) await SettleRefundAsync(ctx, devolucao);
        return Results.Json(ToRefundResource(devolucao), statusCode: 201);
    }

    /// Debits the account that received the Pix; without balance the refund is not made.
    private async Task SettleRefundAsync(ConnectorContext ctx, TDevolucao d)
    {
        var cob = ctx.Store.Get<TCob>(d.TxId)!;
        if (Banking.Accounts(ctx.Store).FirstOrDefault(a => a.Bank == ctx.Connector && a.PixKey == cob.Chave) is { } account
            && !Banking.Debit(ctx, account, d.Valor, "Devolucao Pix", d.RtrId, new() { ["endToEndId"] = d.EndToEndId, ["rtrId"] = d.RtrId }))
        {
            RefuseRefund(ctx, d, "SALDO INSUFICIENTE");
            return;
        }
        d.Liquidacao = ctx.Now;
        ctx.Move(Devolucao, d, DevolucaoStatus.DEVOLVIDO);
        await NotifyKeyAsync(ctx, d.Chave, cob, new { pix = new[] { Notification(ctx, cob) } });
    }

    private IResult RefuseRefund(ConnectorContext ctx, TDevolucao d, string motivo)
    {
        d.Motivo = motivo;
        ctx.Move(Devolucao, d, DevolucaoStatus.NAO_REALIZADO, motivo);
        return Results.Ok(ToRefundResource(d));
    }

    private static IEnumerable<TDevolucao> Refunds(ConnectorContext ctx, string e2eid) =>
        ctx.Store.All<TDevolucao>().Where(d => d.EndToEndId == e2eid).OrderBy(d => d.Solicitacao);

    private static string RefundKey(string e2eid, string id) => $"{e2eid}_{id}";

    private static Dictionary<string, object?> ToRefundResource(TDevolucao d)
    {
        var horario = new Dictionary<string, object?> { ["solicitacao"] = d.Solicitacao };
        if (d.Liquidacao is not null) horario["liquidacao"] = d.Liquidacao;
        var resource = new Dictionary<string, object?>
        {
            ["id"] = d.Id, ["rtrId"] = d.RtrId, ["valor"] = Banking.Money(d.Valor), ["horario"] = horario, ["status"] = d.Status.ToString(),
        };
        if (d.Motivo is not null) resource["motivo"] = d.Motivo;
        return resource;
    }

    [GeneratedRegex("^[a-zA-Z0-9]{1,35}$")] private static partial Regex RefundIdPattern();
}

public sealed record DevolucaoRequest(string? Valor = null, string? Natureza = null, string? Descricao = null);
