using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;

namespace Duora.Web.Accounts;

internal static class SelfieVerificationEndpoints
{
    public static void MapSelfieVerificationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/selfie-verifications").WithTags("Me").ProducesProblem(401).ProducesProblem(403);
        group.MapPost("", async (StartSelfieVerificationRequest request, HttpContext context, SelfieVerificationService service, CancellationToken ct) =>
        {
            var status = await service.RequestAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct);
            return status is null
                ? ApiProblems.Problem(503, "SELFIE_UNAVAILABLE", "Selfie verification unavailable.") as IResult
                : TypedResults.Accepted("/api/v1/me/selfie-verifications/current", Response(status));
        }).Produces<SelfieVerificationResponse>(202).ProducesProblem(503).RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption);
        group.MapGet("/current", async (HttpContext context, SelfieVerificationService service, CancellationToken ct) =>
            TypedResults.Ok(Response(await service.GetCurrentAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct))));
        group.MapPost("/{id:guid}/capture", async (Guid id, SelfieCaptureRequest request, HttpContext context,
            SelfieCaptureCredentialService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = await service.CreateAsync(DuoraClaims.ReadUserId(context.User)!.Value, id, ct);
            return session is null ? TypedResults.NotFound() as IResult : TypedResults.Ok(new SelfieCaptureResponse(
                session.SessionId, session.Region, new(session.Credentials.AccessKeyId, session.Credentials.SecretAccessKey,
                    session.Credentials.SessionToken, session.Credentials.Expiration)));
        }).Produces<SelfieCaptureResponse>(200).Produces(404).RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption);
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteSelfieVerificationRequest request, HttpContext context,
            SelfieVerificationService service, CancellationToken ct) =>
        {
            var status = await service.GetCurrentAsync(DuoraClaims.ReadUserId(context.User)!.Value, ct);
            // Only a hint. The durable worker independently reads the authenticated provider result.
            return status.Id == id ? TypedResults.Accepted("/api/v1/me/selfie-verifications/current", Response(status)) as IResult : TypedResults.NotFound();
        }).Produces<SelfieVerificationResponse>(202).Produces(404).RequireRateLimiting(AuthRateLimitPolicies.TokenRedemption);
    }

    private static SelfieVerificationResponse Response(SelfieStatus status) =>
        new(status.Id, status.State, status.NextStep, status.FeedbackCode, status.ExpiresAt?.UtcDateTime);
}
