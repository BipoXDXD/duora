using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Http;

/// <summary>
/// Erros da API em Problem Details (spec 8.1 e 8.6): <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> estável e
/// <c>traceId</c>, sem detalhe interno. O frontend decide pelo <c>code</c>, nunca pelo texto.
/// </summary>
internal static class ApiProblems
{
    public const string AccountNotEligible = "ACCOUNT_NOT_ELIGIBLE";
    public const string AccountRestricted = "ACCOUNT_RESTRICTED";
    public const string AgeVerificationNotAvailable = "AGE_VERIFICATION_NOT_AVAILABLE";
    public const string AgeVerificationUnavailable = "AGE_VERIFICATION_UNAVAILABLE";
    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyRequestInProgress = "IDEMPOTENCY_REQUEST_IN_PROGRESS";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string LengthRequired = "LENGTH_REQUIRED";
    public const string MediaQueueFull = "MEDIA_QUEUE_FULL";
    public const string MediaUploadsDisabled = "MEDIA_UPLOADS_DISABLED";
    public const string MfaRequired = "MFA_REQUIRED";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string PreconditionFailed = "PRECONDITION_FAILED";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string MfaCodeInvalid = "MFA_CODE_INVALID";
    public const string MfaAlreadyEnabled = "MFA_ALREADY_ENABLED";
    public const string MfaEnrollmentNotStarted = "MFA_ENROLLMENT_NOT_STARTED";
    public const string MfaEnrollmentRequired = "MFA_ENROLLMENT_REQUIRED";
    public const string MfaNotEnabled = "MFA_NOT_ENABLED";
    public const string MfaRequiredByRole = "MFA_REQUIRED_BY_ROLE";
    public const string ReauthenticationRequired = "REAUTHENTICATION_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string SelfBlock = "SELF_BLOCK";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string TemporarilyUnavailable = "TEMPORARILY_UNAVAILABLE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string UploadIncomplete = "UPLOAD_INCOMPLETE";
    public const string UploadPendingLimit = "UPLOAD_PENDING_LIMIT";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string WebhookPayloadInvalid = "WEBHOOK_PAYLOAD_INVALID";
    public const string WebhookSignatureInvalid = "WEBHOOK_SIGNATURE_INVALID";

    public static ProblemHttpResult Problem(int status, string code) =>
        TypedResults.Problem(statusCode: status, title: TitleFor(code), type: TypeFor(code), extensions: CodeExtension(code));

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: TitleFor(ValidationFailed),
            type: TypeFor(ValidationFailed),
            extensions: CodeExtension(ValidationFailed));

    public static ProblemHttpResult TooManyRequests(HttpContext context, TimeSpan retryAfter)
    {
        SetRetryAfter(context, retryAfter);
        return Problem(StatusCodes.Status429TooManyRequests, RateLimited);
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
        var code = problem.Extensions.TryGetValue("code", out var existing) && existing is string known
            ? known
            : problem.Status switch
            {
                StatusCodes.Status400BadRequest => InvalidRequest,
                StatusCodes.Status401Unauthorized => "UNAUTHENTICATED",
                StatusCodes.Status403Forbidden => "FORBIDDEN",
                StatusCodes.Status404NotFound => "NOT_FOUND",
                StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
                StatusCodes.Status411LengthRequired => LengthRequired,
                StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
                StatusCodes.Status429TooManyRequests => RateLimited,
                StatusCodes.Status500InternalServerError => "INTERNAL_ERROR",
                var other => $"HTTP_{other}",
            };
        problem.Extensions["code"] = code;
        problem.Type = TypeFor(code);
        problem.Title = TitleFor(code);
        problem.Detail = null;
    }

    // Human-readable protocol text is fixed by code, independent of request language or endpoint.
    internal static string TitleFor(string code) => code switch
    {
        "ACCOUNT_NOT_ELIGIBLE" => "The account is not eligible.",
        "ACCOUNT_RESTRICTED" => "The account is restricted.",
        "AGE_VERIFICATION_NOT_AVAILABLE" => "Age verification is not available.",
        "AGE_VERIFICATION_UNAVAILABLE" => "Age verification is unavailable.",
        "AGE_VERIFICATION_RETIRED" => "Age verification has been retired.",
        "CSRF_VALIDATION_FAILED" => "Request verification failed.",
        "IDEMPOTENCY_KEY_REUSED" => "The idempotency key was already used for a different request.",
        "IDEMPOTENCY_REQUEST_IN_PROGRESS" => "The request is still in progress.",
        "INVALID_CREDENTIALS" => "Incorrect credentials.",
        "INVALID_REQUEST" => "Invalid request.",
        "INVALID_TOKEN" => "Invalid or expired token.",
        "LENGTH_REQUIRED" => "Content length is required.",
        "MEDIA_QUEUE_FULL" => "The media processing queue is full.",
        "MEDIA_UPLOADS_DISABLED" => "Media uploads are disabled.",
        "MFA_REQUIRED" => "Two-factor authentication is required.",
        "PASSWORD_CHANGE_REQUIRED" => "A password change is required.",
        "PAYLOAD_TOO_LARGE" => "The request payload is too large.",
        "PRECONDITION_FAILED" => "The resource has changed. Reload before saving.",
        "PRECONDITION_REQUIRED" => "A precondition is required.",
        "MFA_CODE_INVALID" => "Invalid two-factor authentication code.",
        "MFA_ALREADY_ENABLED" => "Two-factor authentication is already enabled.",
        "MFA_ENROLLMENT_NOT_STARTED" => "Two-factor authentication setup has not started.",
        "MFA_ENROLLMENT_REQUIRED" => "Two-factor authentication setup is required.",
        "MFA_NOT_ENABLED" => "Two-factor authentication is not enabled.",
        "MFA_REQUIRED_BY_ROLE" => "This role requires two-factor authentication.",
        "REAUTHENTICATION_REQUIRED" => "Reauthentication is required.",
        "RATE_LIMITED" => "Too many attempts. Try again later.",
        "SELF_BLOCK" => "You cannot block yourself.",
        "STORAGE_UNAVAILABLE" => "Storage is temporarily unavailable.",
        "TEMPORARILY_UNAVAILABLE" => "Temporarily unavailable.",
        "UNSUPPORTED_MEDIA_TYPE" => "Unsupported media type.",
        "UPLOAD_INCOMPLETE" => "The upload is incomplete.",
        "UPLOAD_PENDING_LIMIT" => "Too many uploads are pending.",
        "VALIDATION_FAILED" => "The request contains invalid fields.",
        "WEBHOOK_PAYLOAD_INVALID" => "Invalid webhook payload.",
        "WEBHOOK_SIGNATURE_INVALID" => "Invalid webhook signature.",
        "UNAUTHENTICATED" => "Authentication required.",
        "FORBIDDEN" => "Access denied.",
        "NOT_FOUND" => "Resource not found.",
        "METHOD_NOT_ALLOWED" => "Method not allowed.",
        "INTERNAL_ERROR" => "An internal error occurred.",
        "SELFIE_UNAVAILABLE" => "Selfie verification is unavailable.",
        "VERSION_CONFLICT" => "The resource version has changed. Reload before continuing.",
        _ => "Request failed.",
    };

    private static Dictionary<string, object?> CodeExtension(string code) => new() { ["code"] = code };

    private static string TypeFor(string code) =>
        "urn:duora:problem:" + code.ToLowerInvariant().Replace('_', '-');
}
