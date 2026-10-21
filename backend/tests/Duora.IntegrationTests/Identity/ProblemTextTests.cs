using Duora.Web.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Duora.IntegrationTests.Identity;

public sealed class ProblemTextTests
{
    [Theory]
    [InlineData("ACCOUNT_NOT_ELIGIBLE", "The account is not eligible.")]
    [InlineData("ACCOUNT_RESTRICTED", "The account is restricted.")]
    [InlineData("AGE_VERIFICATION_NOT_AVAILABLE", "Age verification is not available.")]
    [InlineData("AGE_VERIFICATION_UNAVAILABLE", "Age verification is unavailable.")]
    [InlineData("AGE_VERIFICATION_RETIRED", "Age verification has been retired.")]
    [InlineData("CSRF_VALIDATION_FAILED", "Request verification failed.")]
    [InlineData("IDEMPOTENCY_KEY_REUSED", "The idempotency key was already used for a different request.")]
    [InlineData("IDEMPOTENCY_REQUEST_IN_PROGRESS", "The request is still in progress.")]
    [InlineData("INVALID_CREDENTIALS", "Incorrect credentials.")]
    [InlineData("INVALID_REQUEST", "Invalid request.")]
    [InlineData("INVALID_TOKEN", "Invalid or expired token.")]
    [InlineData("LENGTH_REQUIRED", "Content length is required.")]
    [InlineData("MEDIA_QUEUE_FULL", "The media processing queue is full.")]
    [InlineData("MEDIA_UPLOADS_DISABLED", "Media uploads are disabled.")]
    [InlineData("MFA_REQUIRED", "Two-factor authentication is required.")]
    [InlineData("PASSWORD_CHANGE_REQUIRED", "A password change is required.")]
    [InlineData("PAYLOAD_TOO_LARGE", "The request payload is too large.")]
    [InlineData("PRECONDITION_FAILED", "The resource has changed. Reload before saving.")]
    [InlineData("PRECONDITION_REQUIRED", "A precondition is required.")]
    [InlineData("MFA_CODE_INVALID", "Invalid two-factor authentication code.")]
    [InlineData("MFA_ALREADY_ENABLED", "Two-factor authentication is already enabled.")]
    [InlineData("MFA_ENROLLMENT_NOT_STARTED", "Two-factor authentication setup has not started.")]
    [InlineData("MFA_ENROLLMENT_REQUIRED", "Two-factor authentication setup is required.")]
    [InlineData("MFA_NOT_ENABLED", "Two-factor authentication is not enabled.")]
    [InlineData("MFA_REQUIRED_BY_ROLE", "This role requires two-factor authentication.")]
    [InlineData("REAUTHENTICATION_REQUIRED", "Reauthentication is required.")]
    [InlineData("RATE_LIMITED", "Too many attempts. Try again later.")]
    [InlineData("SELF_BLOCK", "You cannot block yourself.")]
    [InlineData("STORAGE_UNAVAILABLE", "Storage is temporarily unavailable.")]
    [InlineData("TEMPORARILY_UNAVAILABLE", "Temporarily unavailable.")]
    [InlineData("UNSUPPORTED_MEDIA_TYPE", "Unsupported media type.")]
    [InlineData("UPLOAD_INCOMPLETE", "The upload is incomplete.")]
    [InlineData("UPLOAD_PENDING_LIMIT", "Too many uploads are pending.")]
    [InlineData("VALIDATION_FAILED", "The request contains invalid fields.")]
    [InlineData("WEBHOOK_PAYLOAD_INVALID", "Invalid webhook payload.")]
    [InlineData("WEBHOOK_SIGNATURE_INVALID", "Invalid webhook signature.")]
    [InlineData("UNAUTHENTICATED", "Authentication required.")]
    [InlineData("FORBIDDEN", "Access denied.")]
    [InlineData("NOT_FOUND", "Resource not found.")]
    [InlineData("METHOD_NOT_ALLOWED", "Method not allowed.")]
    [InlineData("INTERNAL_ERROR", "An internal error occurred.")]
    [InlineData("SELFIE_UNAVAILABLE", "Selfie verification is unavailable.")]
    [InlineData("VERSION_CONFLICT", "The resource version has changed. Reload before continuing.")]
    public void ExplicitProblemsUseTheCanonicalEnglishTitle(string code, string expected)
    {
        var problem = ApiProblems.Problem(400, code).ProblemDetails;
        Assert.Equal(expected, problem.Title);
        Assert.Null(problem.Detail);
        Assert.Equal(code, problem.Extensions["code"]);
    }

    [Fact]
    public void EveryDeclaredProblemCodeHasAnExplicitTitle()
    {
        var codes = typeof(ApiProblems).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
        Assert.All(codes, code => Assert.NotEqual("Request failed.", ApiProblems.TitleFor(code)));
    }

    [Fact]
    public void FieldValidationKeepsCodesAndUsesTheEnglishTitle()
    {
        var result = ApiProblems.Validation(new Dictionary<string, string[]> { ["email"] = ["REQUIRED"] });
        Assert.Equal("The request contains invalid fields.", result.ProblemDetails.Title);
        Assert.Equal(["REQUIRED"], result.ProblemDetails.Errors["email"]);
    }

    [Theory]
    [InlineData(400, "INVALID_REQUEST", "Invalid request.")]
    [InlineData(401, "UNAUTHENTICATED", "Authentication required.")]
    [InlineData(403, "FORBIDDEN", "Access denied.")]
    [InlineData(404, "NOT_FOUND", "Resource not found.")]
    [InlineData(405, "METHOD_NOT_ALLOWED", "Method not allowed.")]
    [InlineData(500, "INTERNAL_ERROR", "An internal error occurred.")]
    [InlineData(502, "HTTP_502", "Request failed.")]
    public void FrameworkProblemsReplaceArbitraryTitleAndDetail(int status, string code, string expected)
    {
        var details = new ProblemDetails { Status = status, Title = "CANARY title", Detail = "CANARY internals" };
        ApiProblems.AddDefaultCode(new ProblemDetailsContext { HttpContext = new DefaultHttpContext(), ProblemDetails = details });
        Assert.Equal(code, details.Extensions["code"]);
        Assert.Equal(expected, details.Title);
        Assert.Null(details.Detail);
        Assert.Equal(status, details.Status);
    }

    [Fact]
    public void ExistingCodeStillReceivesCanonicalText()
    {
        var details = new ProblemDetails { Status = 503, Title = "CANARY", Detail = "CANARY" };
        details.Extensions["code"] = "TEMPORARILY_UNAVAILABLE";
        ApiProblems.AddDefaultCode(new ProblemDetailsContext { HttpContext = new DefaultHttpContext(), ProblemDetails = details });
        Assert.Equal("Temporarily unavailable.", details.Title);
        Assert.Null(details.Detail);
    }
}
