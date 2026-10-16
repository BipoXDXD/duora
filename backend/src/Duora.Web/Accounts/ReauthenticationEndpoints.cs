using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

/// <summary>
/// Reautenticação (API-A13) e troca de senha (API-A16). Ficam fora do grupo público <c>/auth</c>: exigem sessão.
/// </summary>
internal static class ReauthenticationEndpoints
{
    public static void MapReauthenticationEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/auth/reauthenticate", ReauthenticateAsync)
            .WithTags("Auth")
            .AllowDuringMfaEnrollment()
            .RequireAccountRateLimit()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/me/password-changes", ChangePasswordAsync)
            .WithTags("Me")
            .AllowDuringPasswordChange()
            .RequireRecentAuthentication()
            .RequireAccountRateLimit()
            .ProducesValidationProblem()
            .ProducesAccountSecurityProblems();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ReauthenticateAsync(
        ReauthenticateRequest request,
        HttpContext context,
        CredentialVerifier credentials,
        AccountSecurityService security,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.ExistingPassword("password", request.Password, out var password);
        string? code = null;
        if (request.TwoFactorCode is not null)
        {
            validation.AuthenticatorCode("twoFactorCode", request.TwoFactorCode, out code);
        }

        if (!validation.IsValid || password is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        var (userId, sessionId) = CurrentSession.Require(context);
        if (await credentials.ReauthenticateAsync(userId, password, code, cancellationToken)
            is not ReauthenticationOutcome.Verified verified)
        {
            return InvalidCredentials();
        }

        await security.RecordReauthenticationAsync(userId, sessionId, verified.WithAuthenticator, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext context,
        AccountSecurityService security,
        IAntiforgery antiforgery,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.ExistingPassword("currentPassword", request.CurrentPassword, out var currentPassword);
        validation.NewPassword("newPassword", request.NewPassword, out var newPassword);
        if (!validation.IsValid || currentPassword is null || newPassword is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        var (userId, _) = CurrentSession.Require(context);
        switch (await security.ChangePasswordAsync(
            userId,
            currentPassword,
            newPassword,
            EmailLanguageNegotiation.FromRequest(context.Request),
            cancellationToken))
        {
            case PasswordChange.Changed changed:
                // Acabou de provar a senha; as provas de segundo fator da sessão continuam valendo na sessão nova.
                var session = CurrentSession.State(context);
                await CurrentSession.RenewAsync(
                    context,
                    antiforgery,
                    changed.SecurityStamp,
                    time.GetUtcNow(),
                    session.MfaVerifiedAt,
                    session.TotpVerifiedAt);
                return TypedResults.NoContent();
            case PasswordChange.WrongPassword:
                return InvalidCredentials();
            case PasswordChange.TemporaryPasswordReused:
                return ApiProblems.Validation(new Dictionary<string, string[]>
                {
                    ["newPassword"] = [RequestValidation.SameAsTemporaryPassword],
                });
            default:
                throw new InvalidOperationException("Resultado da troca de senha não mapeado.");
        }
    }

    private static ProblemHttpResult InvalidCredentials() =>
        ApiProblems.Problem(StatusCodes.Status401Unauthorized, ApiProblems.InvalidCredentials, "Senha ou código inválido.");
}
