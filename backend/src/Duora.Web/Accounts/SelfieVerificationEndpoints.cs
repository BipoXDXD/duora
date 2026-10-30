using Duora.Contracts.Identity;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;

namespace Duora.Web.Accounts;

internal static partial class SelfieVerificationEndpoints
{
    public static void MapSelfieVerificationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/selfie-verifications").WithTags("Me").ProducesProblem(401).ProducesProblem(403);
        group.MapPost("", async (StartSelfieVerificationRequest request, HttpContext context, SelfieVerificationService service,
            TimeProvider time, CancellationToken ct) =>
        {
            var outcome = await service.RequestAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct);
            return outcome switch
            {
                SelfieRequestOutcome.Accepted accepted =>
                    TypedResults.Accepted("/api/v1/me/selfie-verifications/current", Response(accepted.Status)),
                SelfieRequestOutcome.AttemptsLocked locked => Locked(context, locked.Until - time.GetUtcNow()),
                SelfieRequestOutcome.AttemptsExhausted => ApiProblems.Problem(403, ApiProblems.SelfieAttemptsExhausted),
                _ => ApiProblems.Problem(503, "SELFIE_UNAVAILABLE") as IResult,
            };
        }).Produces<SelfieVerificationResponse>(202).ProducesProblem(429).ProducesProblem(503)
            .RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption);
        group.MapGet("/current", async (HttpContext context, SelfieVerificationService service, CancellationToken ct) =>
            TypedResults.Ok(Response(await service.GetCurrentAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct))));
        group.MapPost("/{id:guid}/capture", async (Guid id, SelfieCaptureRequest request, HttpContext context,
            SelfieCaptureCredentialService service, ILoggerFactory loggers, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            SelfieCaptureSession? session;
            try
            {
                session = await service.CreateAsync(DuoraClaims.ReadUserId(context.User)!.Value, id, ct);
            }
            catch (Exception error) when (error is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException)
            {
                // Só o código do erro: a mensagem da AWS pode citar ARN ou chave, e a exceção nunca vai ao log.
                var awsError = error as Amazon.Runtime.AmazonServiceException;
                LogCaptureUnavailable(loggers.CreateLogger(typeof(SelfieVerificationEndpoints)), error.GetType().Name,
                    awsError?.ErrorCode, (int?)awsError?.StatusCode);
                return ApiProblems.Problem(503, "SELFIE_UNAVAILABLE");
            }

            return session is null ? TypedResults.NotFound() as IResult : TypedResults.Ok(new SelfieCaptureResponse(
                session.SessionId, session.Region, new(session.Credentials.AccessKeyId, session.Credentials.SecretAccessKey,
                    session.Credentials.SessionToken, session.Credentials.Expiration)));
        }).Produces<SelfieCaptureResponse>(200).Produces(404).ProducesProblem(429).ProducesProblem(503)
            .RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption)
            .RequireAccountRateLimit(AccountIdOperation.SelfieCapture);
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteSelfieVerificationRequest request, HttpContext context,
            SelfieVerificationService service, CancellationToken ct) =>
        {
            var status = await service.GetCurrentAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct);
            // Only a hint. The durable worker independently reads the authenticated provider result.
            return status.Id == id ? TypedResults.Accepted("/api/v1/me/selfie-verifications/current", Response(status)) as IResult : TypedResults.NotFound();
        }).Produces<SelfieVerificationResponse>(202).Produces(404).RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Selfie capture credentials unavailable ({ErrorType}, {ErrorCode}, {StatusCode})")]
    private static partial void LogCaptureUnavailable(ILogger logger, string errorType, string? errorCode, int? statusCode);

    private static SelfieVerificationResponse Response(SelfieStatus status) =>
        new(status.Id, status.State, status.NextStep, status.FeedbackCode, status.ExpiresAt?.UtcDateTime,
            Attempts(status.Attempts));

    /// <summary>Pausa de 1 hora (issue #300): o <c>Retry-After</c> diz em quantos segundos o segundo ciclo abre.</summary>
    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Locked(HttpContext context, TimeSpan remaining)
    {
        ApiProblems.SetRetryAfter(context, remaining);
        return ApiProblems.Problem(429, ApiProblems.SelfieAttemptsLocked);
    }

    internal static SelfieAttemptsResponse Attempts(SelfieAttemptStanding standing) => standing switch
    {
        SelfieAttemptStanding.Available available => new(nameof(SelfieAttemptStanding.Available), available.Remaining,
            available.LastCycle, null),
        SelfieAttemptStanding.CoolingDown cooling => new(nameof(SelfieAttemptStanding.CoolingDown), 0, true,
            cooling.Until.UtcDateTime),
        SelfieAttemptStanding.Exhausted => new(nameof(SelfieAttemptStanding.Exhausted), 0, true, null),
        _ => throw new InvalidOperationException("Situação de tentativas desconhecida."),
    };
}
