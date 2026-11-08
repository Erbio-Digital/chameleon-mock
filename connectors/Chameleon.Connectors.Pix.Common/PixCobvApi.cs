using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.Pix.Common;

/// Bacen charge with a due date (cobv): same lifecycle as cob, due at the end of dataDeVencimento plus
/// validadeAposVencimento days (Brasilia). The body is validated by the Bacen schema, every violation listed.
public sealed partial class PixCobApi<TCob, TWebhook, TDevolucao>
{
    private static readonly string[] Charges = ["multa", "juros", "abatimento", "desconto"];

    private void MapCobV(IEndpointRouteBuilder routes, Func<ConnectorContext, HttpContext, string, IResult?> authorize)
    {
        var b = _o.BasePath;
        routes.MapPut($"{b}/cobv/{{txid}}", (string txid, JsonElement body, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, _o.CobVWrite) ?? CreateCobV(ctx, http, txid, body));
        routes.MapGet($"{b}/cobv/{{txid}}", (string txid, HttpContext http, ConnectorContext ctx) =>
            authorize(ctx, http, _o.CobVRead) ?? (ctx.Store.Get<TCob>(txid) is { DataDeVencimento: not null } c
                ? Results.Ok(ToCobVResource(ctx, c))
                : Problema.NotFound("CobVNaoEncontrado", $"cobranca com vencimento {txid} nao encontrada")));
    }

    private IResult CreateCobV(ConnectorContext ctx, HttpContext http, string txid, JsonElement body)
    {
        if (CobVViolations(txid, body) is { Count: > 0 } violations)
            return Problema.Of(400, "CobVOperacaoInvalida", "Requisição inválida.", "a cobranca com vencimento nao respeita o schema",
                violations.Select(v => new { razao = v.Razao, propriedade = v.Propriedade }).ToList());

        if (ctx.Store.Get<TCob>(txid) is { } existing)
        {
            if (existing.DataDeVencimento is null)
                return Problema.Invalid("CobVOperacaoInvalida", $"txid {txid} ja utilizado por uma cobranca imediata", "txid");
            http.Annotate(note: "idempotent hit");
            return Results.Json(ToCobVResource(ctx, existing), statusCode: 200);
        }

        PixLoc? loc = null;
        if (_o.Locations is { } locations)
        {
            int? locId = Field(body, "loc") is { ValueKind: JsonValueKind.Object } l && Field(l, "id") is { ValueKind: JsonValueKind.Number } id
                && id.TryGetInt32(out var n) ? n : null;
            if ((loc = locations(ctx, http, locId)) is null)
                return Problema.Invalid("CobVOperacaoInvalida", $"loc {locId} nao encontrado", "loc.id");
        }

        var cal = body.GetProperty("calendario");
        var vencimento = DateOnly.ParseExact(cal.GetProperty("dataDeVencimento").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var validade = Field(cal, "validadeAposVencimento")?.GetInt32() ?? 30;
        var devedor = body.GetProperty("devedor");
        var valor = body.GetProperty("valor");
        var cobranca = new TCob
        {
            TxId = txid,
            ValorOriginal = decimal.Parse(valor.GetProperty("original").GetString()!, CultureInfo.InvariantCulture),
            Chave = body.GetProperty("chave").GetString()!,
            Devedor = new Devedor
            {
                Cpf = Text(devedor, "cpf"), Cnpj = Text(devedor, "cnpj"), Nome = Text(devedor, "nome"), Email = Text(devedor, "email"),
                Logradouro = Text(devedor, "logradouro"), Cidade = Text(devedor, "cidade"), Uf = Text(devedor, "uf"), Cep = Text(devedor, "cep"),
            },
            Loc = loc,
            SolicitacaoPagador = Text(body, "solicitacaoPagador"),
            PixCopiaECola = $"00020126-chameleon-{txid}",
            CriacaoEm = ctx.Now,
            DataDeVencimento = vencimento,
            ValidadeAposVencimento = validade,
            // Payable through the last day of validity; the PSP removes it at the next midnight (Brasilia).
            ExpiraEm = new DateTimeOffset(vencimento.AddDays(validade + 1).ToDateTime(TimeOnly.MinValue), Brasilia.Offset),
            Encargos = Charges.Where(k => Field(valor, k) is not null).ToDictionary(k => k, k => valor.GetProperty(k).Clone()),
        };
        ctx.Create(Cob, cobranca);
        return Results.Json(ToCobVResource(ctx, cobranca), statusCode: 201);
    }

    private static List<(string Propriedade, string Razao)> CobVViolations(string txid, JsonElement body)
    {
        var v = new List<(string, string)>();
        if (!TxIdPattern().IsMatch(txid)) v.Add(("txid", "26-35 alfanumericos"));
        if (body.ValueKind != JsonValueKind.Object) return [.. v, ("cobv", "o corpo deve ser um objeto")];

        if (Object(body, "calendario", v) is { } cal)
        {
            if (Text(cal, "dataDeVencimento") is not { } data
                || !DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                v.Add(("calendario.dataDeVencimento", "obrigatoria, no formato YYYY-MM-DD"));
            if (Field(cal, "validadeAposVencimento") is { } validade && !(validade.ValueKind == JsonValueKind.Number && validade.TryGetInt32(out var d) && d >= 0))
                v.Add(("calendario.validadeAposVencimento", "inteiro maior ou igual a zero"));
        }

        if (Object(body, "devedor", v) is { } devedor)
        {
            var (cpf, cnpj) = (Text(devedor, "cpf"), Text(devedor, "cnpj"));
            if ((cpf is null) == (cnpj is null)) v.Add(("devedor", "informe cpf ou cnpj, um dos dois"));
            else if (cpf is not null && !(cpf.Length == 11 && cpf.All(char.IsAsciiDigit))) v.Add(("devedor.cpf", "11 digitos"));
            else if (cnpj is not null && !(cnpj.Length == 14 && cnpj.All(char.IsAsciiDigit))) v.Add(("devedor.cnpj", "14 digitos"));
            if (Text(devedor, "nome") is not { Length: <= 200 }) v.Add(("devedor.nome", "obrigatorio, ate 200 caracteres"));
        }

        if (Object(body, "valor", v) is { } valor)
        {
            if (Text(valor, "original") is not { } original || !ValorPattern().IsMatch(original) || decimal.Parse(original, CultureInfo.InvariantCulture) == 0)
                v.Add(("valor.original", "string \\d{1,10}.\\d{2} maior que zero"));
            foreach (var k in Charges)
                if (Field(valor, k) is { ValueKind: not JsonValueKind.Object })
                    v.Add(($"valor.{k}", "deve ser um objeto"));
        }

        if (Text(body, "chave") is not { Length: <= 77 }) v.Add(("chave", "obrigatoria, ate 77 caracteres"));
        return v;
    }

    private object ToCobVResource(ConnectorContext ctx, TCob c)
    {
        var valor = new Dictionary<string, object?> { ["original"] = Banking.Money(c.ValorOriginal) };
        foreach (var (k, e) in c.Encargos ?? []) valor[k] = e;
        var d = c.Devedor!;
        var devedor = new Dictionary<string, object?> { [d.Cpf is not null ? "cpf" : "cnpj"] = d.Cpf ?? d.Cnpj, ["nome"] = d.Nome };
        foreach (var (k, v) in new[] { ("email", d.Email), ("logradouro", d.Logradouro), ("cidade", d.Cidade), ("uf", d.Uf), ("cep", d.Cep) })
            if (v is not null) devedor[k] = v;
        var resource = new Dictionary<string, object?>
        {
            ["calendario"] = new
            {
                criacao = c.CriacaoEm, dataDeVencimento = c.DataDeVencimento!.Value.ToString("yyyy-MM-dd"), validadeAposVencimento = c.ValidadeAposVencimento,
            },
            ["txid"] = c.TxId,
            ["revisao"] = c.Revisao,
            ["status"] = c.Status.ToString(),
            ["devedor"] = devedor,
            ["valor"] = valor,
            ["chave"] = c.Chave,
            ["solicitacaoPagador"] = c.SolicitacaoPagador,
            ["pixCopiaECola"] = c.PixCopiaECola,
            ["pix"] = c.Status == CobrancaStatus.CONCLUIDA ? new object[] { PixItem(ctx, c) } : [],
        };
        if (c.Loc is { } loc) resource["loc"] = loc;
        return resource;
    }

    /// The property, or null when absent or JSON null.
    private static JsonElement? Field(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Text(JsonElement obj, string name) =>
        Field(obj, name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s ? s : null;

    private static JsonElement? Object(JsonElement obj, string name, List<(string, string)> violations)
    {
        var field = Field(obj, name);
        if (field is { ValueKind: JsonValueKind.Object }) return field;
        violations.Add((name, field is null ? "obrigatorio" : "deve ser um objeto"));
        return null;
    }
}
