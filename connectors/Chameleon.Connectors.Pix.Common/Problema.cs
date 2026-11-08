using Microsoft.AspNetCore.Http;

namespace Chameleon.Connectors.Pix.Common;

/// Errors in the Bacen API Pix shape (RFC 7807), which Bradesco and Inter follow.
public static class Problema
{
    public static IResult Of(int status, string type, string title, string? detail = null, object? violacoes = null) =>
        Results.Json(new
        {
            type = $"https://pix.bcb.gov.br/api/v2/error/{type}",
            title,
            status,
            detail,
            correlationId = Guid.NewGuid().ToString(),
            violacoes,
        }, statusCode: status, contentType: "application/problem+json");

    public static IResult Invalid(string type, string detail, string? propriedade = null) =>
        Of(400, type, "Requisição inválida.", detail, propriedade is null ? null : new[] { new { razao = detail, propriedade } });

    public static IResult NotFound(string type, string detail) => Of(404, type, "Não encontrado.", detail);
}
