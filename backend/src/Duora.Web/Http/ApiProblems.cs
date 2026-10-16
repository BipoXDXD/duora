using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Http;

/// <summary>
/// Erros da API em Problem Details (spec 8.1 e 8.6): <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> estável e
/// <c>traceId</c>, sem detalhe interno. O frontend decide pelo <c>code</c>, nunca pelo texto.
/// </summary>
internal static class ApiProblems
{
    public const string AgeVerificationNotAvailable = "AGE_VERIFICATION_NOT_AVAILABLE";
    public const string AgeVerificationUnavailable = "AGE_VERIFICATION_UNAVAILABLE";
    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string MfaRequired = "MFA_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string ValidationFailed = "VALIDATION_FAILED";

    public static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(statusCode: status, title: title, type: TypeFor(code), extensions: CodeExtension(code));

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "A requisição tem campos inválidos.",
            type: TypeFor(ValidationFailed),
            extensions: CodeExtension(ValidationFailed));

    public static ProblemHttpResult TooManyRequests(HttpContext context, TimeSpan retryAfter)
    {
        SetRetryAfter(context, retryAfter);
        return Problem(StatusCodes.Status429TooManyRequests, RateLimited, "Muitas tentativas. Tente novamente mais tarde.");
    }

    public static void SetRetryAfter(HttpContext context, TimeSpan retryAfter) =>
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Completa erros produzidos pelo framework (JSON malformado, 401 do desafio, 404 de rota, 500) com um
    /// <c>code</c> derivado do status, para todo erro seguir o mesmo contrato.
    /// </summary>
    public static void AddDefaultCode(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        if (problem.Extensions.ContainsKey("code"))
        {
            return;
        }

        var code = problem.Status switch
        {
            StatusCodes.Status400BadRequest => "INVALID_REQUEST",
            StatusCodes.Status401Unauthorized => "UNAUTHENTICATED",
            StatusCodes.Status403Forbidden => "FORBIDDEN",
            StatusCodes.Status404NotFound => "NOT_FOUND",
            StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
            StatusCodes.Status413PayloadTooLarge => "PAYLOAD_TOO_LARGE",
            StatusCodes.Status415UnsupportedMediaType => "UNSUPPORTED_MEDIA_TYPE",
            StatusCodes.Status429TooManyRequests => RateLimited,
            StatusCodes.Status500InternalServerError => "INTERNAL_ERROR",
            var other => $"HTTP_{other}",
        };
        problem.Extensions["code"] = code;
        problem.Type = TypeFor(code);
    }

    private static Dictionary<string, object?> CodeExtension(string code) => new() { ["code"] = code };

    private static string TypeFor(string code) =>
        "urn:duora:problem:" + code.ToLowerInvariant().Replace('_', '-');
}
