using Duora.Web.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Duora.IntegrationTests.Identity;

public sealed class ProblemTextTests
{
    public static TheoryData<string, string> PublicTitles => new()
    {
        { "ACCOUNT_NOT_ELIGIBLE", "The account is not eligible for this operation." },
        { "ACCOUNT_RESTRICTED", "The account cannot perform this operation." },
        { "ACCOUNT_SUSPENDED", "The account is suspended." },
        { "ACCOUNT_ALREADY_SUSPENDED", "The account already has an active suspension." },
        { "ACCOUNT_NOT_SUSPENDABLE", "The account cannot be suspended in its current state." },
        { "ACCOUNT_MODERATION_FORBIDDEN", "Account moderation requires the moderator role." },
        { "AGE_VERIFICATION_NOT_AVAILABLE", "Age verification is not available." },
        { "AGE_VERIFICATION_UNAVAILABLE", "Age verification is temporarily unavailable." },
        { "CSRF_VALIDATION_FAILED", "Request rejected." },
        { "IDEMPOTENCY_KEY_REUSED", "This idempotency key has already been used for another request." },
        { "IDEMPOTENCY_REQUEST_IN_PROGRESS", "The request with this idempotency key is still in progress." },
        { "INVALID_CREDENTIALS", "Invalid credentials." },
        { "INVALID_REQUEST", "Invalid request." },
        { "INVALID_TOKEN", "Invalid or expired link." },
        { "LENGTH_REQUIRED", "Content-Length is required." },
        { "MEDIA_QUEUE_FULL", "Photo processing is busy. Try again later." },
        { "MEDIA_UPLOADS_DISABLED", "Photo uploads are disabled." },
        { "MFA_REQUIRED", "A second authentication factor is required." },
        { "PASSWORD_CHANGE_REQUIRED", "Change your temporary password to continue." },
        { "PHOTO_DELETE_STATE_CONFLICT", "The photo cannot be deleted while it is still being uploaded. Try again when the upload finishes." },
        { "PAYLOAD_TOO_LARGE", "Request payload is too large." },
        { "PRECONDITION_FAILED", "The resource has changed. Reload it before saving." },
        { "PRECONDITION_REQUIRED", "Provide the ETag from the last read in If-Match." },
        { "MFA_CODE_INVALID", "Invalid authentication code." },
        { "MFA_ALREADY_ENABLED", "MFA is already enabled." },
        { "MFA_ENROLLMENT_NOT_STARTED", "Start MFA enrollment before confirming." },
        { "MFA_ENROLLMENT_REQUIRED", "Enroll a second authentication factor to continue." },
        { "MFA_NOT_ENABLED", "MFA is not enabled." },
        { "MFA_REQUIRED_BY_ROLE", "MFA is required for this account role." },
        { "REAUTHENTICATION_REQUIRED", "Confirm your credentials to continue." },
        { "RATE_LIMITED", "Too many requests. Try again later." },
        { "SELF_BLOCK", "You cannot block your own account." },
        { "SELFIE_ATTEMPTS_EXHAUSTED", "Selfie attempts are exhausted. The account is blocked pending review." },
        { "SELFIE_ATTEMPTS_LOCKED", "Too many rejected selfies. Try again after the pause." },
        { "STORAGE_UNAVAILABLE", "Photo storage is temporarily unavailable. Retry with the same key." },
        { "TEMPORARILY_UNAVAILABLE", "Service temporarily unavailable. Try again later." },
        { "UNSUPPORTED_MEDIA_TYPE", "Unsupported media type." },
        { "UPLOAD_INCOMPLETE", "The upload is incomplete. Retry with the same key." },
        { "UPLOAD_PENDING_LIMIT", "Too many photos are pending. Wait for processing to finish." },
        { "VALIDATION_FAILED", "The request contains invalid fields." },
        { "WEBHOOK_PAYLOAD_INVALID", "Invalid webhook payload." },
        { "WEBHOOK_SIGNATURE_INVALID", "Invalid webhook signature." },
        { "UNAUTHENTICATED", "Authentication required." },
        { "FORBIDDEN", "Forbidden." },
        { "NOT_FOUND", "Resource not found." },
        { "METHOD_NOT_ALLOWED", "Method not allowed." },
        { "INTERNAL_ERROR", "An unexpected error occurred." },
        { "AGE_VERIFICATION_RETIRED", "Age verification has been retired." },
        { "SELFIE_UNAVAILABLE", "Selfie verification unavailable." },
        { "VERSION_CONFLICT", "The resource has changed. Reload it before continuing." },
    };

    [Theory]
    [MemberData(nameof(PublicTitles))]
    public void EveryPublicCodeHasTheExactEnglishText(string code, string title)
    {
        var problem = ApiProblems.Problem(400, code).ProblemDetails;
        Assert.Equal(title, problem.Title);
        Assert.Equal(code, problem.Extensions["code"]);
        Assert.Equal(400, problem.Status);
        Assert.Equal("urn:duora:problem:" + code.ToLowerInvariant().Replace('_', '-'), problem.Type);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public void AllDeclaredCodesAreCoveredByThePublicTextContract()
    {
        var expectedCodes = PublicTitles.Select(row => row.Data.Item1).ToHashSet(StringComparer.Ordinal);
        var codes = typeof(ApiProblems).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
        Assert.All(codes, code => Assert.Contains(code, expectedCodes));
    }

    [Fact]
    public void RateLimitPreservesStatusAndRetryAfter()
    {
        var http = new DefaultHttpContext();
        var problem = ApiProblems.TooManyRequests(http, TimeSpan.FromSeconds(1.2)).ProblemDetails;
        Assert.Equal(429, problem.Status);
        Assert.Equal("RATE_LIMITED", problem.Extensions["code"]);
        Assert.Equal("Too many requests. Try again later.", problem.Title);
        Assert.Equal("2", http.Response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("email", "INVALID_FORMAT")]
    [InlineData("body", "A foto está vazia.")]
    [InlineData("Idempotency-Key", "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.")]
    public void ValidationUsesEnglishAndPreservesFieldValues(string field, string value)
    {
        var errors = new Dictionary<string, string[]> { [field] = [value] };
        var problem = ApiProblems.Validation(errors).ProblemDetails;
        Assert.Equal("The request contains invalid fields.", problem.Title);
        Assert.Equal(400, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Extensions["code"]);
        Assert.Equal(errors[field], problem.Errors[field]);
        Assert.Null(problem.Detail);
    }

    [Theory]
    [InlineData(400, "INVALID_REQUEST", "Invalid request.")]
    [InlineData(401, "UNAUTHENTICATED", "Authentication required.")]
    [InlineData(403, "FORBIDDEN", "Forbidden.")]
    [InlineData(404, "NOT_FOUND", "Resource not found.")]
    [InlineData(405, "METHOD_NOT_ALLOWED", "Method not allowed.")]
    [InlineData(411, "LENGTH_REQUIRED", "Content-Length is required.")]
    [InlineData(413, "PAYLOAD_TOO_LARGE", "Request payload is too large.")]
    [InlineData(415, "UNSUPPORTED_MEDIA_TYPE", "Unsupported media type.")]
    [InlineData(429, "RATE_LIMITED", "Too many requests. Try again later.")]
    [InlineData(500, "INTERNAL_ERROR", "An unexpected error occurred.")]
    [InlineData(503, "HTTP_503", "The request could not be completed.")]
    [InlineData(418, "HTTP_418", "The request could not be completed.")]
    public void FrameworkErrorsDiscardArbitraryText(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = "CANARY title", Detail = "CANARY SQL and stack" };
        ApiProblems.AddDefaultCode(new ProblemDetailsContext { HttpContext = new DefaultHttpContext(), ProblemDetails = problem });
        Assert.Equal(status, problem.Status);
        Assert.Equal(code, problem.Extensions["code"]);
        Assert.Equal(title, problem.Title);
        Assert.Null(problem.Detail);
        Assert.Equal("urn:duora:problem:" + code.ToLowerInvariant().Replace('_', '-'), problem.Type);
    }

    [Theory]
    [InlineData("INVALID_TOKEN", "Invalid or expired link.")]
    [InlineData("TEMPORARILY_UNAVAILABLE", "Service temporarily unavailable. Try again later.")]
    [InlineData("UNKNOWN", "The request could not be completed.")]
    public void ExistingCodesArePreservedButTextIsNormalized(string code, string title)
    {
        var problem = new ProblemDetails { Status = 400, Title = "CANARY", Detail = "CANARY" };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = "trace";
        ApiProblems.AddDefaultCode(new ProblemDetailsContext { HttpContext = new DefaultHttpContext(), ProblemDetails = problem });
        Assert.Equal(code, problem.Extensions["code"]);
        Assert.Equal("trace", problem.Extensions["traceId"]);
        Assert.Equal(400, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Null(problem.Detail);
    }
}
