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
    public const string PhotoDeleteStateConflict = "PHOTO_DELETE_STATE_CONFLICT";
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
        var code = problem.Extensions.TryGetValue("code", out var existing) && existing is string supplied
            ? supplied
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

    // Explicit public text only: never use exception messages or request values.
    private static string TitleFor(string code) => code switch
    {
        AccountNotEligible => "The account is not eligible for this operation.",
        AccountRestricted => "The account cannot perform this operation.",
        AgeVerificationNotAvailable => "Age verification is not available.",
        AgeVerificationUnavailable => "Age verification is temporarily unavailable.",
        CsrfValidationFailed => "Request rejected.",
        IdempotencyKeyReused => "This idempotency key has already been used for another request.",
        IdempotencyRequestInProgress => "The request with this idempotency key is still in progress.",
        InvalidCredentials => "Invalid credentials.",
        InvalidRequest => "Invalid request.",
        InvalidToken => "Invalid or expired link.",
        LengthRequired => "Content-Length is required.",
        MediaQueueFull => "Photo processing is busy. Try again later.",
        MediaUploadsDisabled => "Photo uploads are disabled.",
        MfaRequired => "A second authentication factor is required.",
        PasswordChangeRequired => "Change your temporary password to continue.",
        PayloadTooLarge => "Request payload is too large.",
        PhotoDeleteStateConflict => "The photo cannot be deleted while it is being processed or reviewed. Try again later.",
        PreconditionFailed => "The resource has changed. Reload it before saving.",
        PreconditionRequired => "Provide the ETag from the last read in If-Match.",
        MfaCodeInvalid => "Invalid authentication code.",
        MfaAlreadyEnabled => "MFA is already enabled.",
        MfaEnrollmentNotStarted => "Start MFA enrollment before confirming.",
        MfaEnrollmentRequired => "Enroll a second authentication factor to continue.",
        MfaNotEnabled => "MFA is not enabled.",
        MfaRequiredByRole => "MFA is required for this account role.",
        ReauthenticationRequired => "Confirm your credentials to continue.",
        RateLimited => "Too many requests. Try again later.",
        SelfBlock => "You cannot block your own account.",
        StorageUnavailable => "Photo storage is temporarily unavailable. Retry with the same key.",
        TemporarilyUnavailable => "Service temporarily unavailable. Try again later.",
        UnsupportedMediaType => "Unsupported media type.",
        UploadIncomplete => "The upload is incomplete. Retry with the same key.",
        UploadPendingLimit => "Too many photos are pending. Wait for processing to finish.",
        ValidationFailed => "The request contains invalid fields.",
        WebhookPayloadInvalid => "Invalid webhook payload.",
        WebhookSignatureInvalid => "Invalid webhook signature.",
        "UNAUTHENTICATED" => "Authentication required.",
        "FORBIDDEN" => "Forbidden.",
        "NOT_FOUND" => "Resource not found.",
        "METHOD_NOT_ALLOWED" => "Method not allowed.",
        "INTERNAL_ERROR" => "An unexpected error occurred.",
        "AGE_VERIFICATION_RETIRED" => "Age verification has been retired.",
        "SELFIE_UNAVAILABLE" => "Selfie verification unavailable.",
        "VERSION_CONFLICT" => "The resource has changed. Reload it before continuing.",
        _ => "The request could not be completed.",
    };

    private static Dictionary<string, object?> CodeExtension(string code) => new() { ["code"] = code };

    private static string TypeFor(string code) =>
        "urn:duora:problem:" + code.ToLowerInvariant().Replace('_', '-');
}
