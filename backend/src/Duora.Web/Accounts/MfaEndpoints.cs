using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

/// <summary>
/// MFA do titular (API-A11, A12, A14 e A15; ADR-0017), para participante e conta privilegiada pelas mesmas rotas.
/// Todas exigem prova recente (o A14, de TOTP) e respeitam o limite por conta. Segredos (chave e códigos) só aparecem na
/// resposta que os provisiona, sempre com <c>no-store</c>, e nunca vão a log.
/// </summary>
internal static class MfaEndpoints
{
    public static void MapMfaEndpoints(this IEndpointRouteBuilder api)
    {
        var mfa = api.MapGroup("/me/mfa").WithTags("Me");

        mfa.MapPost("/enrollments", StartEnrollmentAsync)
            .AllowDuringMfaEnrollment()
            .RequireRecentAuthentication()
            .RequireAccountRateLimit()
            .ProducesAccountSecurityProblems()
            .ProducesProblem(StatusCodes.Status409Conflict);
        mfa.MapPost("/confirmations", ConfirmEnrollmentAsync)
            .AllowDuringMfaEnrollment()
            .RequireRecentAuthentication()
            .RequireAccountRateLimit()
            .ProducesAccountSecurityProblems()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        mfa.MapPost("/recovery-codes", RegenerateRecoveryCodesAsync)
            .RequireAccountSession()
            .RequireRecentMfa()
            .RequireAccountRateLimit()
            .ProducesAccountSecurityProblems()
            .ProducesProblem(StatusCodes.Status409Conflict);
        mfa.MapDelete("", DisableAsync)
            .RequireAccountSession()
            .RequireRecentAuthentication()
            .RequireAccountRateLimit()
            .ProducesAccountSecurityProblems();
    }

    /// <summary>401 sem sessão ou sem autenticação recente, 403 de CSRF ou de papel e 429 do limite por sessão.</summary>
    internal static RouteHandlerBuilder ProducesAccountSecurityProblems(this RouteHandlerBuilder route) =>
        route
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static async Task<Results<Ok<MfaEnrollmentResponse>, ProblemHttpResult>> StartEnrollmentAsync(
        HttpContext context,
        AccountSecurityService security,
        CancellationToken cancellationToken)
    {
        var (userId, sessionId) = CurrentSession.Require(context);
        return await security.StartMfaEnrollmentAsync(userId, sessionId, cancellationToken) switch
        {
            MfaEnrollmentStart.Started started =>
                TypedResults.Ok(new MfaEnrollmentResponse(started.SharedKey, started.AuthenticatorUri)),
            MfaEnrollmentStart.AlreadyEnabled => AlreadyEnabled(),
            var other => throw new InvalidOperationException($"Resultado não mapeado: {other}."),
        };
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, ValidationProblem, ProblemHttpResult>> ConfirmEnrollmentAsync(
        ConfirmMfaRequest request,
        HttpContext context,
        AccountSecurityService security,
        IAntiforgery antiforgery,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        if (!validation.AuthenticatorCode("code", request.Code, out var code))
        {
            return ApiProblems.Validation(validation.Errors);
        }

        var (userId, _) = CurrentSession.Require(context);
        switch (await security.ConfirmMfaEnrollmentAsync(userId, code, cancellationToken))
        {
            case MfaActivation.Activated activated:
                // Acabou de provar o TOTP: a sessão nova nasce com a prova de MFA e a autenticação recente.
                var now = time.GetUtcNow();
                await CurrentSession.RenewAsync(context, antiforgery, activated.SecurityStamp, now, now, now);
                return TypedResults.Ok(new RecoveryCodesResponse(activated.RecoveryCodes));
            case MfaActivation.InvalidCode:
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, ApiProblems.MfaCodeInvalid, "Código inválido.");
            case MfaActivation.NotStarted:
                return ApiProblems.Problem(
                    StatusCodes.Status409Conflict,
                    ApiProblems.MfaEnrollmentNotStarted,
                    "Comece o cadastro do autenticador antes de confirmar.");
            case MfaActivation.AlreadyEnabled:
                return AlreadyEnabled();
            default:
                throw new InvalidOperationException("Resultado de ativação não mapeado.");
        }
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult>> RegenerateRecoveryCodesAsync(
        HttpContext context,
        AccountSecurityService security,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentSession.Require(context);
        return await security.RegenerateRecoveryCodesAsync(userId, cancellationToken) switch
        {
            RecoveryCodesRegeneration.Regenerated regenerated =>
                TypedResults.Ok(new RecoveryCodesResponse(regenerated.RecoveryCodes)),
            RecoveryCodesRegeneration.MfaNotEnabled => NotEnabled(),
            var other => throw new InvalidOperationException($"Resultado não mapeado: {other}."),
        };
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DisableAsync(
        HttpContext context,
        AccountSecurityService security,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentSession.Require(context);
        switch (await security.DisableMfaAsync(userId, cancellationToken))
        {
            case MfaDeactivation.Disabled disabled:
                var session = CurrentSession.State(context);
                await CurrentSession.RenewAsync(
                    context,
                    antiforgery,
                    disabled.SecurityStamp,
                    session.ReauthenticatedAt,
                    mfaVerifiedAt: null,
                    totpVerifiedAt: null);
                return TypedResults.NoContent();
            case MfaDeactivation.AlreadyDisabled:
                return TypedResults.NoContent();
            case MfaDeactivation.RequiredByRole:
                return ApiProblems.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiProblems.MfaRequiredByRole,
                    "O papel desta conta exige MFA.");
            default:
                throw new InvalidOperationException("Resultado de desativação não mapeado.");
        }
    }

    private static ProblemHttpResult AlreadyEnabled() =>
        ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.MfaAlreadyEnabled, "O MFA já está ativo.");

    private static ProblemHttpResult NotEnabled() =>
        ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.MfaNotEnabled, "O MFA não está ativo.");
}
