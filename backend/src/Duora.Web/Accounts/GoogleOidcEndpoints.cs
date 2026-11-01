using Duora.Contracts.Identity;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Web.Accounts;

internal static class GoogleOidcEndpoints
{
    public static void MapGoogleOidcEndpoints(this IEndpointRouteBuilder api)
    {
        var publicRoutes = api.MapGroup("/auth/google").AllowAnonymous().WithTags("Auth")
            .WithMetadata(new PublicAntiforgeryEndpoint());
        publicRoutes.MapGet("/options", (IOptions<GoogleOidcSettings> settings) =>
            new GoogleAuthenticationOptions(settings.Value.Enabled, settings.Value.Enabled && settings.Value.RegistrationEnabled,
                settings.Value.Enabled && settings.Value.LinkingEnabled));
        publicRoutes.MapGet("/flow", async (HttpContext c, ExternalAuthenticationService service, IOptions<GoogleOidcSettings> settings, TimeProvider time, CancellationToken ct) =>
        {
            var f = settings.Value.Enabled ? await service.ViewAsync(Cookie(c), DuoraClaims.ReadSessionId(c.User), ct) : new ExternalFlowView("Failed", time.GetUtcNow());
            return new GoogleFlowResponse(f.NextStep, f.ExpiresAt.UtcDateTime, f.Email);
        });
        publicRoutes.MapPost("/requests", StartAsync).RequireRateLimiting(AuthRateLimitPolicies.Login)
            .Produces<GoogleAuthorizationResponse>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
        publicRoutes.MapGet("/authorize", AuthorizeAsync).Produces(302).ProducesProblem(400).ProducesProblem(404);
        publicRoutes.MapPost("/completions", CompleteAsync).RequireRateLimiting(AuthRateLimitPolicies.Login)
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
        publicRoutes.MapPost("/registrations", RegisterAsync).RequireRateLimiting(AuthRateLimitPolicies.Register)
            .Produces(202).ProducesValidationProblem().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
        publicRoutes.MapPost("/registration-confirmations", ConfirmAsync).RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption)
            .Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
        var owner = api.MapGroup("/me/external-logins").WithTags("Me").RequireAccountSession();
        owner.MapGet("", async (HttpContext c, ExternalAuthenticationService service, CancellationToken ct) =>
        {
            var (user, _) = CurrentSession.Require(c);
            var items = await service.ListAsync(user, ct);
            return new ExternalLoginsResponse(items.Select(i => new ExternalLoginResponse(i.Provider, i.LinkedAt.UtcDateTime)).ToArray());
        }).Produces<ExternalLoginsResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);
        owner.MapPost("/google/requests", LinkRequestAsync).RequireRecentAuthentication().RequireAccountRateLimit()
            .Produces<GoogleAuthorizationResponse>().ProducesAccountSecurityProblems().ProducesProblem(404);
        owner.MapPost("/google/confirmations", LinkAsync).RequireRecentAuthentication().RequireAccountRateLimit()
            .Produces(204).ProducesAccountSecurityProblems().ProducesProblem(404);
        owner.MapDelete("/google", UnlinkAsync).AllowForSuspendedAccount().RequireRecentAuthentication().RequireAccountRateLimit()
            .Produces(204).ProducesAccountSecurityProblems().ProducesProblem(404);
    }

    private static string? Cookie(HttpContext c) => c.Request.Cookies[GoogleOidcAuthentication.FlowCookie];
    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult InvalidFlow() => ApiProblems.Problem(400, "EXTERNAL_FLOW_INVALID");
    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Disabled() => ApiProblems.Problem(404, "NOT_FOUND");
    private static void SetCookie(HttpContext c, string value) => c.Response.Cookies.Append(GoogleOidcAuthentication.FlowCookie, value,
        new CookieOptions { Secure = true, HttpOnly = true, Path = "/", SameSite = SameSiteMode.Lax, MaxAge = ExternalAuthenticationService.Lifetime });

    private static async Task<IResult> StartAsync(GoogleAuthenticationRequest request, HttpContext c,
        ExternalAuthenticationService service, IOptions<GoogleOidcSettings> settings, CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            return Disabled();
        }

        if (request.Purpose is not ("Login" or "Register"))
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidRequest);
        }

        if (request.Purpose == "Register" && !settings.Value.RegistrationEnabled)
        {
            return Disabled();
        }

        if (c.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblems.Problem(403, "FORBIDDEN");
        }

        var cookie = await service.CreateAsync(request.Purpose, null, null, ct);
        if (cookie is null)
        {
            return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable);
        }

        SetCookie(c, cookie);
        return Results.Ok(new GoogleAuthorizationResponse("/api/v1/auth/google/authorize"));
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext c, ExternalAuthenticationService service,
        IOptions<GoogleOidcSettings> settings, CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            return Disabled();
        }

        var id = await service.ChallengeAsync(Cookie(c), ct);
        if (id is null)
        {
            return InvalidFlow();
        }

        var properties = new AuthenticationProperties();
        properties.Items[GoogleOidcAuthentication.FlowProperty] = id.Value.ToString("D");
        return Results.Challenge(properties, [GoogleOidcAuthentication.Scheme]);
    }

    private static async Task<IResult> CompleteAsync(GoogleCompletionRequest request, HttpContext c,
        ExternalAuthenticationService service, IOptions<GoogleOidcSettings> settings, IAntiforgery antiforgery,
        TimeProvider time, CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            return Disabled();
        }

        if (c.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblems.Problem(403, "FORBIDDEN");
        }

        var validation = new RequestValidation();
        SecondFactorProof? factor = null;
        if (request.TwoFactorCode is not null && request.RecoveryCode is not null)
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidRequest);
        }

        if (request.TwoFactorCode is not null && validation.AuthenticatorCode("twoFactorCode", request.TwoFactorCode, out var code))
        {
            factor = new SecondFactorProof.AuthenticatorCode(code);
        }

        if (request.RecoveryCode is not null && validation.RecoveryCode("recoveryCode", request.RecoveryCode, out var recovery))
        {
            factor = new SecondFactorProof.RecoveryCode(recovery);
        }

        if (!validation.IsValid)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        var result = await service.CompleteLoginAsync(Cookie(c), factor, ct);
        if (result.Account is not { } account)
        {
            return ApiProblems.Problem(401, result.Outcome == "MfaRequired" ? ApiProblems.MfaRequired : ApiProblems.InvalidCredentials);
        }

        var now = time.GetUtcNow();
        await AuthEndpoints.StartSessionAsync(c, antiforgery, new SessionGrant(account.UserId, account.SecurityStamp, SessionPolicy.Normal,
            ReauthenticatedAt: null, MfaVerifiedAt: account.SecondFactor == SecondFactorKind.None ? null : now,
            TotpVerifiedAt: account.SecondFactor == SecondFactorKind.Authenticator ? now : null, AuthenticationMethod: "GoogleOidc"));
        return Results.NoContent();
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, HttpContext c, AccountService accounts,
        ExternalAuthenticationService service, LegalPublication legal, AccountAttemptLimiter limiter,
        IOptions<GoogleOidcSettings> settings, CancellationToken ct)
    {
        if (!settings.Value.Enabled || !settings.Value.RegistrationEnabled)
        {
            return Disabled();
        }

        if (c.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblems.Problem(403, "FORBIDDEN");
        }

        var validation = new RequestValidation();
        validation.NewPassword("password", request.Password, out var password);
        validation.DocumentVersion("acceptedTermsVersion", request.AcceptedTermsVersion, out var terms);
        validation.DocumentVersion("acceptedPrivacyVersion", request.AcceptedPrivacyVersion, out var privacy);
        validation.DocumentLocale("acceptedTermsLocale", request.AcceptedTermsLocale);
        validation.DocumentLocale("acceptedPrivacyLocale", request.AcceptedPrivacyLocale);
        validation.Confirmed("confirmedAdult", request.ConfirmedAdult);
        if (!validation.IsValid || password is null || terms is null || privacy is null)
        {
            return ApiProblems.Validation(validation.Errors);
        }

        if (!legal.RegistrationAvailable)
        {
            return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable);
        }

        if (!legal.Accepts(terms, privacy, request.AcceptedTermsLocale, request.AcceptedPrivacyLocale, request.AcceptedTermsSha256, request.AcceptedPrivacySha256))
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidRequest);
        }

        var flow = await service.ViewAsync(Cookie(c), DuoraClaims.ReadSessionId(c.User), ct);
        if (flow.NextStep != "Register" || !EmailAddress.TryParse(flow.Email, out var email))
        {
            return InvalidFlow();
        }

        if (request.Email is not null && request.Email != email.Value)
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidRequest);
        }

        if (limiter.TryConsume(AccountOperation.Register, email) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(c, retryAfter);
        }

        try
        {
            await service.RegisterAsync(Cookie(c), accounts, password, terms, privacy, EmailLanguageNegotiation.FromRequest(c.Request),
                request.AcceptedTermsLocale, request.AcceptedPrivacyLocale, request.AcceptedTermsSha256, request.AcceptedPrivacySha256, ct);
            return Results.Accepted();
        }
        catch (ExternalFlowInvalidException) { return InvalidFlow(); }
    }

    private static async Task<IResult> ConfirmAsync(GoogleRegistrationConfirmation request, HttpContext c,
        AccountService accounts, ExternalAuthenticationService service, IOptions<GoogleOidcSettings> settings, CancellationToken ct)
    {
        if (!settings.Value.Enabled || !settings.Value.RegistrationEnabled)
        {
            return Disabled();
        }

        if (c.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblems.Problem(403, "FORBIDDEN");
        }

        var validation = new RequestValidation();
        validation.ExistingPassword("password", request.Password, out var password);
        if (request.Code is not { Length: 8 } || !request.Code.All(char.IsAsciiDigit) || password is null || !validation.IsValid)
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidToken);
        }

        try
        {
            return await service.ConfirmRegistrationAsync(Cookie(c), accounts, request.Code, password, ct)
                ? Results.NoContent() : ApiProblems.Problem(400, ApiProblems.InvalidToken);
        }
        catch (DbUpdateException ex) when (IsConflict(ex)) { return ApiProblems.Problem(400, ApiProblems.InvalidToken); }
    }

    private static async Task<IResult> LinkRequestAsync(GoogleAuthenticationRequest request, HttpContext c,
        ExternalAuthenticationService service, IOptions<GoogleOidcSettings> settings, CancellationToken ct)
    {
        if (!settings.Value.Enabled || !settings.Value.LinkingEnabled)
        {
            return Disabled();
        }

        if (request.Purpose is not (null or "Link"))
        {
            return ApiProblems.Problem(400, ApiProblems.InvalidRequest);
        }

        var (user, session) = CurrentSession.Require(c);
        if (CurrentSession.State(c).AccountIsPrivileged)
        {
            return ApiProblems.Problem(403, "FORBIDDEN");
        }

        var cookie = await service.CreateAsync("Link", user, session, ct);
        if (cookie is null)
        {
            return ApiProblems.Problem(401, ApiProblems.ReauthenticationRequired);
        }

        SetCookie(c, cookie);
        return Results.Ok(new GoogleAuthorizationResponse("/api/v1/auth/google/authorize"));
    }

    private static async Task<IResult> LinkAsync(ConfirmExternalLoginRequest _, HttpContext c, ExternalAuthenticationService service,
        IOptions<GoogleOidcSettings> settings, IAntiforgery antiforgery, CancellationToken ct)
    {
        if (!settings.Value.Enabled || !settings.Value.LinkingEnabled)
        {
            return Disabled();
        }

        var (user, session) = CurrentSession.Require(c);
        var state = CurrentSession.State(c);
        try
        {
            var result = await service.LinkAsync(Cookie(c), user, session, ct);
            if (result.SecurityStamp is { } stamp)
            {
                await CurrentSession.RenewAsync(c, antiforgery, stamp, state.ReauthenticatedAt, state.MfaVerifiedAt, state.TotpVerifiedAt);
                return Results.NoContent();
            }
            return result.Outcome == "Conflict" ? ApiProblems.Problem(409, "EXTERNAL_LINK_UNAVAILABLE") : InvalidFlow();
        }
        catch (DbUpdateException ex) when (IsConflict(ex)) { return ApiProblems.Problem(409, "EXTERNAL_LINK_UNAVAILABLE"); }
    }

    private static async Task<IResult> UnlinkAsync(HttpContext c, ExternalAuthenticationService service,
        IOptions<GoogleOidcSettings> settings, IAntiforgery antiforgery, CancellationToken ct)
    {
        if (!settings.Value.Enabled)
        {
            return Disabled();
        }

        var (user, session) = CurrentSession.Require(c);
        var state = CurrentSession.State(c);
        var result = await service.UnlinkAsync(user, session, ct);
        if (result.SecurityStamp is { } stamp)
        {
            await CurrentSession.RenewAsync(c, antiforgery, stamp, state.ReauthenticatedAt, state.MfaVerifiedAt, state.TotpVerifiedAt);
        }

        return result.Outcome is "Completed" or "Absent" ? Results.NoContent() : ApiProblems.Problem(401, ApiProblems.ReauthenticationRequired);
    }

    private static bool IsConflict(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
