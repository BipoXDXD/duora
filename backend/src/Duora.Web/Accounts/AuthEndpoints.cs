using System.Diagnostics;
using System.Security.Claims;
using Duora.Contracts.Identity;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

/// <summary>
/// Rotas públicas de autenticação (API-A01 a API-A07). "Pública" não dispensa CSRF, rate limit nem resposta
/// neutra: nenhuma delas diz se um e-mail tem conta.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").AllowAnonymous().WithTags("Auth");

        auth.MapGet("/csrf", IssueCsrfToken).WithMetadata(new PublicAntiforgeryEndpoint());
        auth.MapPost("/register", RegisterAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.Register)
            .ProducesMutationProblems()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        auth.MapPost("/confirm-email", ConfirmEmailAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption)
            .ProducesMutationProblems()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        auth.MapPost("/email-confirmation-requests", RequestEmailConfirmationAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.Register)
            .ProducesMutationProblems()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        auth.MapPost("/login", LoginAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.Login)
            .ProducesMutationProblems()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        auth.MapPost("/logout", LogoutAsync)
            .WithMetadata(new SessionAwareLogoutEndpoint())
            .ProducesProblem(StatusCodes.Status403Forbidden);
        auth.MapPost("/password-reset-requests", RequestPasswordResetAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.PasswordResetRequest)
            .ProducesMutationProblems();
        auth.MapPost("/password-resets", ResetPasswordAsync)
            .WithMetadata(new PublicAntiforgeryEndpoint())
            .RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption)
            .ProducesMutationProblems();
    }

    /// <summary>403 de CSRF e 429 de rate limit, comuns às mutações públicas, documentados no contrato.</summary>
    private static RouteHandlerBuilder ProducesMutationProblems(this RouteHandlerBuilder route) =>
        route
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static NoContent IssueCsrfToken(HttpContext context, IAntiforgery antiforgery)
    {
        AntiforgeryTokens.Issue(context, antiforgery);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        HttpContext context,
        AccountService accounts,
        LegalPublication legal,
        AccountAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.Email("email", request.Email, out var email);
        validation.NewPassword("password", request.Password, out var password);
        validation.DocumentVersion("acceptedTermsVersion", request.AcceptedTermsVersion, out var termsVersion);
        validation.DocumentVersion("acceptedPrivacyVersion", request.AcceptedPrivacyVersion, out var privacyVersion);
        validation.DocumentLocale("acceptedTermsLocale", request.AcceptedTermsLocale);
        validation.DocumentLocale("acceptedPrivacyLocale", request.AcceptedPrivacyLocale);
        validation.Confirmed("confirmedAdult", request.ConfirmedAdult);
        if (!validation.IsValid || email is null || password is null || termsVersion is null || privacyVersion is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        if (!legal.RegistrationAvailable)
        {
            return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
        }
        if (!legal.Accepts(termsVersion, privacyVersion, request.AcceptedTermsLocale, request.AcceptedPrivacyLocale, request.AcceptedTermsSha256, request.AcceptedPrivacySha256))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["acceptedTermsVersion"] = ["INVALID_FORMAT"],
                ["acceptedPrivacyVersion"] = ["INVALID_FORMAT"],
                ["acceptedTermsLocale"] = ["INVALID_FORMAT"],
                ["acceptedPrivacyLocale"] = ["INVALID_FORMAT"],
                ["acceptedTermsSha256"] = ["INVALID_FORMAT"],
                ["acceptedPrivacySha256"] = ["INVALID_FORMAT"],
            });
        }

        if (limiter.TryConsume(AccountOperation.Register, email) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }

        await accounts.RegisterAsync(
            email,
            password,
            termsVersion,
            privacyVersion,
            EmailLanguageNegotiation.FromRequest(context.Request),
            cancellationToken,
            request.AcceptedTermsLocale,
            request.AcceptedPrivacyLocale,
            request.AcceptedTermsSha256,
            request.AcceptedPrivacySha256);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, ValidationProblem, ProblemHttpResult>> RequestEmailConfirmationAsync(
        CreateEmailConfirmationRequest request,
        HttpContext context,
        AccountService accounts,
        AccountAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.Email("email", request.Email, out var email);
        validation.ExistingPassword("password", request.Password, out var password);
        if (!validation.IsValid || email is null || password is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }
        // Mesmo contador do cadastro: a nova rota não multiplica cotas por endereço/conta ou o envio persistido.
        if (limiter.TryConsume(AccountOperation.Register, email) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }
        var startedAt = Stopwatch.GetTimestamp();
        await accounts.RequestEmailConfirmationAsync(email, password, cancellationToken);
        // Piso monotônico e jitter para todos os 202; fora da transação, sem ocupar conexão durante a espera.
        var floor = TimeSpan.FromMilliseconds(500 + Random.Shared.Next(101));
        while (floor - Stopwatch.GetElapsedTime(startedAt) is var remaining && remaining > TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), cancellationToken);
        }
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        HttpContext context,
        AccountService accounts,
        AccountSecurityAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.Present("userId", request.UserId, out var userId);
        validation.Token("token", request.Token, out var token);
        validation.ExistingPassword("password", request.Password, out var password);
        if (!validation.IsValid || token is null || password is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        // Sem login automático: confirmar só muda o estado da conta. Abrir sessão aqui seria um segundo caminho de
        // SignIn para proteger contra fixation; a SPA leva ao login, que já faz isso.
        return await accounts.ConfirmEmailAsync(userId, token, password, cancellationToken)
            ? TypedResults.NoContent()
            : CountFailure(context, limiter, AccountIdOperation.EmailConfirmationFailure, userId);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext context,
        CredentialVerifier credentials,
        AccountAttemptLimiter limiter,
        IAntiforgery antiforgery,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.Email("email", request.Email, out var email);
        validation.ExistingPassword("password", request.Password, out var password);
        var secondFactor = ReadSecondFactor(request, validation);
        if (!validation.IsValid || email is null || password is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        if (limiter.TryConsume(AccountOperation.Login, email) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }

        switch (await credentials.VerifyAsync(email, password, secondFactor, cancellationToken))
        {
            case LoginOutcome.Verified { Account: var account }:
                var now = time.GetUtcNow();
                await StartSessionAsync(
                    context,
                    antiforgery,
                    new SessionGrant(
                        account.UserId,
                        account.SecurityStamp,
                        account.Policy,
                        ReauthenticatedAt: now,
                        MfaVerifiedAt: account.SecondFactor == SecondFactorKind.None ? null : now,
                        TotpVerifiedAt: account.SecondFactor == SecondFactorKind.Authenticator ? now : null));
                return TypedResults.NoContent();
            case LoginOutcome.MfaRequired:
                return ApiProblems.Problem(
                    StatusCodes.Status401Unauthorized,
                    ApiProblems.MfaRequired);
            default:
                return ApiProblems.Problem(
                    StatusCodes.Status401Unauthorized,
                    ApiProblems.InvalidCredentials);
        }
    }

    /// <summary>Segundo fator opcional do login: um dos dois códigos, nunca ambos.</summary>
    private static SecondFactorProof? ReadSecondFactor(LoginRequest request, RequestValidation validation)
    {
        if (!validation.Exclusive("recoveryCode", request.TwoFactorCode is not null && request.RecoveryCode is not null))
        {
            return null;
        }

        if (request.TwoFactorCode is not null)
        {
            return validation.AuthenticatorCode("twoFactorCode", request.TwoFactorCode, out var code)
                ? new SecondFactorProof.AuthenticatorCode(code)
                : null;
        }

        if (request.RecoveryCode is not null)
        {
            return validation.RecoveryCode("recoveryCode", request.RecoveryCode, out var code)
                ? new SecondFactorProof.RecoveryCode(code)
                : null;
        }

        return null;
    }

    /// <summary>
    /// Sem sessão válida, logout também é 204: o estado final (sem sessão) já é o pedido, e não há o que registrar.
    /// Com sessão, a revogação e o registro de auditoria (TM-05) confirmam juntos antes de o cookie sair.
    /// </summary>
    private static async Task<NoContent> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AccountSecurityService security,
        CancellationToken cancellationToken)
    {
        if ((DuoraClaims.ReadUserId(context.User), DuoraClaims.ReadSessionId(context.User)) is ({ } userId, { } sessionId))
        {
            await security.RecordLogoutAsync(userId, sessionId, cancellationToken);
        }

        await context.SignOutAsync(DuoraAuthentication.Scheme);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        AntiforgeryTokens.Issue(context, antiforgery);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted, ValidationProblem, ProblemHttpResult>> RequestPasswordResetAsync(
        CreatePasswordResetRequest request,
        HttpContext context,
        AccountService accounts,
        AccountAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        if (!validation.Email("email", request.Email, out var email))
        {
            return ApiProblems.Validation(validation.Errors);
        }

        if (limiter.TryConsume(AccountOperation.PasswordResetRequest, email) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }

        await accounts.RequestPasswordResetAsync(email, EmailLanguageNegotiation.FromRequest(context.Request), cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ResetPasswordAsync(
        ResetPasswordRequest request,
        HttpContext context,
        AccountService accounts,
        AccountSecurityAttemptLimiter limiter,
        CancellationToken cancellationToken)
    {
        var validation = new RequestValidation();
        validation.Present("userId", request.UserId, out var userId);
        validation.Token("token", request.Token, out var token);
        validation.NewPassword("newPassword", request.NewPassword, out var newPassword);
        if (!validation.IsValid || token is null || newPassword is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        return await accounts.ResetPasswordAsync(
                userId,
                token,
                newPassword,
                EmailLanguageNegotiation.FromRequest(context.Request),
                cancellationToken)
            ? TypedResults.NoContent()
            : CountFailure(context, limiter, AccountIdOperation.PasswordResetFailure, userId);
    }

    /// <summary>
    /// Conta a falha de um resgate por token só depois de conferi-lo: o link válido do titular passa mesmo com a cota
    /// esgotada por terceiros, que só veem o 429 no lugar do 400.
    /// </summary>
    private static ProblemHttpResult CountFailure(
        HttpContext context,
        AccountSecurityAttemptLimiter limiter,
        AccountIdOperation operation,
        Guid userId) =>
        limiter.TryConsume(operation, userId) is { } retryAfter
            ? ApiProblems.TooManyRequests(context, retryAfter)
            : InvalidToken();

    /// <summary>
    /// Encerra a sessão que veio na requisição antes de abrir a nova (session fixation): a chave anterior é revogada
    /// no banco e a nova é aleatória. A ordem é a garantia: o SignOut vem antes do SignIn, sempre (ver
    /// <see cref="DuoraAuthentication.SignInScheme"/>). O token antiforgery é reemitido para a identidade nova.
    /// </summary>
    internal static async Task StartSessionAsync(HttpContext context, IAntiforgery antiforgery, SessionGrant grant)
    {
        await context.SignOutAsync(DuoraAuthentication.Scheme);
        var principal = DuoraClaims.ForSignIn(grant);
        await context.SignInAsync(
            DuoraAuthentication.SignInScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        context.User = principal;
        AntiforgeryTokens.Issue(context, antiforgery);
    }

    private static ProblemHttpResult InvalidToken() =>
        ApiProblems.Problem(StatusCodes.Status400BadRequest, ApiProblems.InvalidToken);
}
