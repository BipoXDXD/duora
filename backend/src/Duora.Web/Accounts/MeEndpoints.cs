using Duora.Contracts.Identity;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Accounts;

/// <summary>
/// Conta, sessões e novo aceite dos Termos do titular (API-A08 a API-A10 e API-A17). Exigem sessão pela política
/// padrão de autorização.
/// </summary>
internal static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Me");
        var sessions = me.MapGroup("/sessions").RequireAccountSession();

        me.MapGet("", GetMeAsync)
            .WithMetadata(new SessionContextBootstrapEndpoint())
            .AllowDuringMfaEnrollment()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .Produces<PrivateSessionRestrictionResponse>(StatusCodes.Status403Forbidden, "application/problem+json")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(DocumentRestrictedSession);
        me.MapPost("/terms-acceptances", AcceptTermsAsync)
            .RequireAccountSession()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        sessions.MapGet("", ListSessionsAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        sessions.MapDelete("/{sessionId:guid}", RevokeSessionAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>A SPA decide a fase restrita por este 403 (auth.md, "SPA: senha inicial administrativa").</summary>
    private static Task DocumentRestrictedSession(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses?.TryGetValue("403", out var forbidden) == true && forbidden is OpenApiResponse response)
        {
            response.Description =
                "PASSWORD_CHANGE_REQUIRED: a sessão foi aberta com a senha temporária do bootstrap e só alcança a "
                + "troca de senha (API-A16). Conta privilegiada sem MFA recebe 200, com mfaEnabled false e os papéis.";
        }

        return Task.CompletedTask;
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMeAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AccountService accounts,
        LocaleStore locales,
        TimeZoneStore timeZones,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || await accounts.FindAccountAsync(userId, cancellationToken) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new MeResponse(
            account.Id,
            account.Email,
            account.Status.ToString(),
            account.EmailConfirmed,
            account.MfaEnabled,
            account.Roles,
            account.CurrentTermsAccepted,
            PrivateOperationContext.Issue(context, antiforgery),
            (await locales.GetOwnAsync(userId, cancellationToken))?.Locale,
            (await timeZones.GetOwnAsync(userId, cancellationToken))?.TimeZone));
    }

    /// <summary>
    /// Novo aceite dos Termos ativos com a declaração de 18 anos ou mais (API-A17). Recibo que não é o dos Termos
    /// ativos, ou declaração ausente, <c>null</c> ou <c>false</c>, responde 400 sem gravar. Repetir responde 204 e
    /// mantém o primeiro aceite. Não existe recurso alheio aqui: a conta é a da sessão.
    /// </summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AcceptTermsAsync(
        AcceptTermsRequest request,
        HttpContext context,
        AccountService accounts,
        LegalPublication legal,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentSession.Require(context);
        var validation = new RequestValidation();
        validation.DocumentVersion("acceptedTermsVersion", request.AcceptedTermsVersion, out var termsVersion);
        validation.DocumentLocale("acceptedTermsLocale", request.AcceptedTermsLocale);
        validation.Confirmed("confirmedAdult", request.ConfirmedAdult);
        if (!validation.IsValid || termsVersion is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        if (!legal.RegistrationAvailable)
        {
            return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
        }

        if (!legal.AcceptsTerms(termsVersion, request.AcceptedTermsLocale, request.AcceptedTermsSha256)
            || request.AcceptedTermsLocale is not { } termsLocale
            || request.AcceptedTermsSha256 is not { } termsSha256)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["acceptedTermsVersion"] = [RequestValidation.InvalidFormat],
                ["acceptedTermsLocale"] = [RequestValidation.InvalidFormat],
                ["acceptedTermsSha256"] = [RequestValidation.InvalidFormat],
            });
        }

        await accounts.AcceptCurrentTermsAsync(userId, termsLocale, termsSha256, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SessionListResponse>, UnauthorizedHttpResult>> ListSessionsAsync(
        HttpContext context,
        LoginSessions sessions,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var currentSessionId = DuoraClaims.ReadSessionId(context.User);
        var active = await sessions.ListActiveAsync(userId, cancellationToken);
        var items = active
            .Select(session => new SessionResponse(
                session.Id,
                session.CreatedAt.UtcDateTime,
                session.LastSeenAt.UtcDateTime,
                session.Id == currentSessionId,
                session.Description))
            .ToList();
        return TypedResults.Ok(new SessionListResponse(items));
    }

    /// <summary>Sessão de outra pessoa e sessão inexistente dão o mesmo 404 (spec 8.1).</summary>
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult, ProblemHttpResult>> RevokeSessionAsync(
        Guid sessionId,
        HttpContext context,
        AccountSecurityService security,
        AccountSecurityAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (await security.RevokeOwnSessionAsync(userId, sessionId, cancellationToken))
        {
            // Remediação: revogar uma sessão própria e ativa nunca conta na cota, nem a da sessão que se revoga.
            return TypedResults.NoContent();
        }

        return limiter.TryConsume(AccountIdOperation.SessionRevocationFailure, userId) is { } retryAfter
            ? ApiProblems.TooManyRequests(context, retryAfter)
            : TypedResults.NotFound();
    }
}
