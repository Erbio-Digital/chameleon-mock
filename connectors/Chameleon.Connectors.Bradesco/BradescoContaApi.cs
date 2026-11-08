using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;

namespace Chameleon.Connectors.Bradesco;

/// Bradesco balance and statement (fornecimento de saldos/extratos), projected from the sandbox ledger
/// in the shape clients parse: values pt-BR, sign apart, tipoLancamento 2 (credit) or 9
/// (debit) (to confirm with the bank), dates dd/MM/yyyy in Brasilia.
internal static class BradescoContaApi
{
    private static readonly NumberFormatInfo PtBr = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = [3] };

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/bradesco/v1/fornecimento-saldos-contas/saldos", (HttpContext http, ConnectorContext ctx) =>
        {
            if (BradescoConnector.Refuse(ctx, http) is { } refused) return refused;
            if (Account(ctx, http.Request.Query) is not { } account) return AccountNotFound;
            var saldo = account.Balance;
            return Results.Ok(new
            {
                saldoCC = new
                {
                    lstLancamentosSaldos = new[]
                    {
                        new
                        {
                            nomeProduto = "SALDO TOTAL", nomeProdutoResumido = "SALDO TOTAL", codigoProduto = 1, identificadorSaldo = "T",
                            dataLancamentoDb2 = Brasilia.DateOf(ctx.Now).ToString("dd.MM.yyyy"), valorLancamento = Money(saldo), sinalSaldo = Sign(saldo),
                        },
                    },
                },
            });
        });

        routes.MapGet("/bradesco/v1/fornecimento-extratos-contas/extratos", (HttpContext http, ConnectorContext ctx) =>
        {
            if (BradescoConnector.Refuse(ctx, http) is { } refused) return refused;
            var q = http.Request.Query;
            if (!TryDate(q["dataInicio"], out var inicio) || !TryDate(q["dataFim"], out var fim) || inicio > fim || q["tipo"] != "cc")
                return Results.BadRequest(new { mensagem = "dataInicio e dataFim (ddMMyyyy, inicio <= fim) e tipo=cc obrigatorios" });
            if (Account(ctx, q) is not { } account) return AccountNotFound;

            var today = Brasilia.DateOf(ctx.Now);
            var running = 0m;
            var anterior = 0m;
            var period = new List<(DateOnly Day, object Item)>();
            foreach (var e in account.Entries.OrderBy(e => e.At))
            {
                running += e.Amount;
                var day = Brasilia.DateOf(e.At);
                if (day < inicio) anterior = running;
                else if (day <= fim) period.Add((day, Item(e, day, running)));
            }

            // Clients concatenate every list, so an entry shows in only one: past days in the period list, today in Lancamentos Dia.
            var count = period.Count.ToString(CultureInfo.InvariantCulture);
            return Results.Ok(new
            {
                extratoUltimosLancamentos = new
                {
                    quantidadeLancamentos = count,
                    listaLancamentos = new Dictionary<string, object[]>
                    {
                        ["Saldo Anterior"] = [new
                        {
                            dataLancamento = inicio.AddDays(-1).ToString("dd/MM/yyyy"), valorLancamento = Money(anterior), sinalLancamento = Sign(anterior),
                            valorSaldoAposLancamento = Money(anterior), sinalSaldo = Sign(anterior), descritivoLancamentoAbreviado = "SALDO ANTERIOR",
                        }],
                        ["Ultimos Lancamentos"] = [],
                        ["Lancamentos Dia"] = period.Where(p => p.Day == today).Select(p => p.Item).ToArray(),
                    },
                },
                extratoPorPeriodo = new
                {
                    quantidadeLancamentos = count,
                    lstLancamentoMensal = period.Where(p => p.Day != today).Select(p => p.Item).ToArray(),
                },
            });
        });
    }

    private static object Item(LedgerEntry e, DateOnly day, decimal saldo) => new
    {
        tipoLancamento = e.Amount >= 0 ? "2" : "9",
        codigoLancamento = e.Id,
        dataLancamento = day.ToString("dd/MM/yyyy"),
        dataLancamentoDb2 = day.ToString("dd.MM.yyyy"),
        numeroDocumento = e.Reference ?? e.Id,
        valorLancamento = Money(e.Amount),
        sinalLancamento = Sign(e.Amount),
        segundaLinhalLancamento = e.Reference,
        valorSaldoAposLancamento = Money(saldo),
        sinalSaldo = Sign(saldo),
        descritivoLancamentoAbreviado = e.Description.ToUpperInvariant(),
        descritivoLancamentoCompleto = e.Reference is null ? e.Description.ToUpperInvariant() : $"{e.Description.ToUpperInvariant()} {e.Reference}",
    };

    private static BankAccount? Account(ConnectorContext ctx, IQueryCollection q) =>
        BradescoConnector.FindAccount(ctx, q["agencia"], q["conta"], thisBank: true);

    private static readonly IResult AccountNotFound = Results.Json(new { mensagem = "conta nao encontrada" }, statusCode: 404);

    private static string Money(decimal value) => Math.Abs(value).ToString("#,##0.00", PtBr);

    private static string Sign(decimal value) => value < 0 ? "-" : "+";

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
