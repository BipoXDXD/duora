using Duora.Web.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Duora.Web.Media;

/// <summary>Apresentação por pedido das duas recusas de headers; não altera a decisão nem a recepção do upload.</summary>
internal static class PhotoValidationProblems
{
    public const string HeaderName = "Duora-Validation-Format";
    public const string CodesV1 = "codes-v1";

    public static ValidationProblem EmptyBody(HttpRequest request) =>
        Validation(request, "body", "A foto está vazia.", "PHOTO_BODY_EMPTY");

    public static ValidationProblem InvalidIdempotencyKey(HttpRequest request) =>
        Validation(request, "Idempotency-Key", "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.", "IDEMPOTENCY_KEY_INVALID");

    private static ValidationProblem Validation(HttpRequest request, string field, string legacy, string code)
    {
        var codes = request.Headers.TryGetValue(HeaderName, out var values)
            && values.Count == 1
            && string.Equals(values[0]?.Trim(' ', '\t'), CodesV1, StringComparison.Ordinal);
        var response = request.HttpContext.Response;
        if (!response.Headers.GetCommaSeparatedValues(HeaderNames.Vary).Any(value => value == "*"
            || string.Equals(value, HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderName);
        }
        if (codes)
        {
            response.Headers[HeaderName] = CodesV1;
        }
        return ApiProblems.Validation(new Dictionary<string, string[]> { [field] = [codes ? code : legacy] });
    }
}
