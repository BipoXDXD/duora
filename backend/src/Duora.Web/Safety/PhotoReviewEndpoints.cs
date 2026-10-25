using Duora.Contracts.Media;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Npgsql;

namespace Duora.Web.Safety;

internal static class PhotoReviewEndpoints
{
    internal static void MapPhotoReviewOwnerEndpoints(this IEndpointRouteBuilder api)
    {
        var owner = api.MapGroup("/me/photos").WithTags("Photos");
        Read(owner.MapGet("", OwnerPhotosAsync)).Produces<PhotoReviewPhotoPageResponse>()
            .WithDescription("Owner-only photo discovery, ordered by creation time and photo ID. Limit 1..50, default 20; the opaque cursor is bound to this authenticated owner. No images or storage locators.");
        Read(owner.MapGet("/{photoId:guid}/review-history", HistoryAsync))
            .Produces<PhotoReviewHistoryResponse>();
        Command(owner.MapPost("/{photoId:guid}/appeals", RequestAsync))
            .Produces<PhotoAppealRequestedResponse>(201);
    }

    // The caller passes the actual administrative group; its existing session/MFA policy applies to all routes.
    internal static void MapPhotoReviewAdminEndpoints(this RouteGroupBuilder admin)
    {
        var cases = admin.MapGroup("/photo-reviews").WithTags("PhotoReviews");
        Read(cases.MapGet("/capabilities", CapabilitiesAsync)).Produces<PhotoReviewCapabilitiesResponse>();
        Read(cases.MapGet("", CasesAsync)).Produces<PhotoReviewCasePageResponse>();
        Read(cases.MapGet("/{caseId:guid}", CaseAsync)).Produces<PhotoReviewCaseResponse>();
        Command(cases.MapPost("/{caseId:guid}/assignments", AssignCaseAsync))
            .Produces<PhotoReviewMutationResponse>();
        Read(cases.MapGet("/{caseId:guid}/evidence/{variant}", CaseEvidenceAsync)).Produces(200, contentType: "image/webp");
        Command(cases.MapPost("/{caseId:guid}/decisions", DecideCaseAsync))
            .RequireRecentMfa().Produces<PhotoReviewDecisionResponse>();

        var appeals = admin.MapGroup("/photo-appeals").WithTags("PhotoAppeals");
        Read(appeals.MapGet("", AppealsAsync)).Produces<PhotoAppealPageResponse>();
        Read(appeals.MapGet("/{appealId:guid}", AppealAsync)).Produces<PhotoAppealResponse>();
        Command(appeals.MapPost("/{appealId:guid}/assignments", AssignAppealAsync)).Produces<PhotoAppealMutationResponse>();
        Read(appeals.MapGet("/{appealId:guid}/evidence/{variant}", AppealEvidenceAsync)).Produces(200, contentType: "image/webp");
        Command(appeals.MapPost("/{appealId:guid}/decisions", DecideAppealAsync))
            .RequireRecentMfa().Produces<PhotoReviewDecisionResponse>();
    }

    private static RouteHandlerBuilder Read(RouteHandlerBuilder route) => route
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503)
        .AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            // Existing media admission remains authoritative. Registering routes does not admit a new policy.
            if (!http.RequestServices.GetRequiredService<PhotoUploads>().UploadsEnabled)
            {
                return ApiProblems.Problem(503, ApiProblems.MediaUploadsDisabled);
            }
            try
            {
                return await next(invocation);
            }
            catch (ArgumentException)
            {
                return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return Unavailable(http);
            }
            catch (IOException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return Unavailable(http);
            }
            catch (PostgresException error) when (!http.RequestAborted.IsCancellationRequested
                && (error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.QueryCanceled))
            {
                ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
                return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable);
            }
        });

    private static RouteHandlerBuilder Command(RouteHandlerBuilder route) => Read(route)
        .ProducesProblem(409).ProducesProblem(412).ProducesProblem(428)
        .AddOpenApiOperationTransformer(DocumentCommandHeaders);

    private static Task DocumentCommandHeaders(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "If-Match",
            In = ParameterLocation.Header,
            Required = true,
            Description = "The exact opaque strong ETag of this photo, case or appeal; tags of other resources never match.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "A stable key for this exact command intention. Authenticated replay observes the committed receipt.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 64, Pattern = "^[A-Za-z0-9_-]+$" },
        });
        return Task.CompletedTask;
    }

    private static async Task<IResult> HistoryAsync(Guid photoId, HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        var actor = CurrentSession.Require(http);
        var result = await service.HistoryAsync(actor.UserId, actor.SessionId, photoId, ct);
        if (result.Value is not { } history)
        {
            return Refused(http, result.Refusal);
        }
        var tag = PhotoReviewHeaders.Tag("photo", photoId, history.PhotoVersion);
        http.Response.Headers.ETag = tag;
        return Results.Ok(new PhotoReviewHistoryResponse(photoId, tag, history.Items.Select(i =>
            new PhotoReviewHistoryItemResponse(i.DecisionId, i.Decision, i.ReasonCode, i.DecidedAt, i.IsAppeal)).ToArray(),
            history.Appeal is { } appeal ? Appeal(appeal) : null, history.CanRequestAppeal));
    }

    private static async Task<IResult> OwnerPhotosAsync([FromQuery] int? limit, [FromQuery] string? cursor,
        HttpContext http, PhotoReviewService service, IDataProtectionProvider protection, CancellationToken ct)
    {
        var actor = CurrentSession.Require(http);
        if (limit is < 1 or > 50 || (cursor is null && http.Request.Query.ContainsKey("cursor"))
            || !OwnerPhotoCursor.TryRead(protection, actor.UserId, cursor, out var after))
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        var result = await service.OwnerPhotosAsync(actor.UserId, actor.SessionId, after, limit ?? 20, ct);
        return result.Value is { } page
            ? Results.Ok(new PhotoReviewPhotoPageResponse(page.Items.Select(p =>
                new PhotoReviewPhotoResponse(p.PhotoId, p.Status, p.RejectionCode, p.CreatedAt)).ToArray(),
                OwnerPhotoCursor.Write(protection, actor.UserId, page.NextCursor)))
            : Refused(http, result.Refusal);
    }

    private static async Task<IResult> RequestAsync(Guid photoId, HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count != 0)
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        if (PhotoReviewHeaders.Command(http, "photo", photoId, out var version, out var key) is { } refused)
        {
            return refused;
        }
        var actor = CurrentSession.Require(http);
        var result = await service.RequestAppealAsync(actor.UserId, actor.SessionId, photoId, version, key, ct);
        if (result.Value is not { } value || value.AppealId is not { } appealId)
        {
            return Refused(http, result.Refusal);
        }
        return Results.Created($"/api/v1/me/photos/{photoId}/review-history", new PhotoAppealRequestedResponse(
            appealId, photoId, PhotoReviewHeaders.Tag("photo", photoId, value.PhotoVersion),
            value.RequestedAt!.Value, value.ResponseTargetAt!.Value, value.MaximumRetentionUntil!.Value));
    }

    private static async Task<IResult> CapabilitiesAsync(HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        var actor = CurrentSession.Require(http);
        var result = await service.CapabilitiesAsync(actor.UserId, actor.SessionId, ct);
        return result.Value is { } value
            ? Results.Ok(new PhotoReviewCapabilitiesResponse(value.Assign, value.Evidence, value.Decide,
                value.Appeal, value.RecentMfaRequiredForDecision)) : Refused(http, result.Refusal);
    }

    private static async Task<IResult> CasesAsync([FromQuery] int? limit, [FromQuery] string? cursor,
        HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        if (limit is < 1 or > 50)
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        var actor = CurrentSession.Require(http);
        var result = await service.CasesAsync(actor.UserId, actor.SessionId, cursor, limit ?? 20, ct);
        return result.Value is { } page
            ? Results.Ok(new PhotoReviewCasePageResponse(page.Items.Select(Case).ToArray(), page.NextCursor))
            : Refused(http, result.Refusal);
    }

    private static async Task<IResult> CaseAsync(Guid caseId, HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        var actor = CurrentSession.Require(http);
        var result = await service.CaseAsync(actor.UserId, actor.SessionId, caseId, ct);
        if (result.Value is not { } item)
        {
            return Refused(http, result.Refusal);
        }
        var body = Case(item);
        http.Response.Headers.ETag = body.ETag;
        return Results.Ok(body);
    }

    private static async Task<IResult> AppealsAsync([FromQuery] int? limit, [FromQuery] string? cursor,
        HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        if (limit is < 1 or > 50)
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        var actor = CurrentSession.Require(http);
        var result = await service.AppealsAsync(actor.UserId, actor.SessionId, cursor, limit ?? 20, ct);
        return result.Value is { } page
            ? Results.Ok(new PhotoAppealPageResponse(page.Items.Select(Appeal).ToArray(), page.NextCursor))
            : Refused(http, result.Refusal);
    }

    private static async Task<IResult> AppealAsync(Guid appealId, HttpContext http, PhotoReviewService service, CancellationToken ct)
    {
        var actor = CurrentSession.Require(http);
        var result = await service.AppealAsync(actor.UserId, actor.SessionId, appealId, ct);
        if (result.Value is not { } item)
        {
            return Refused(http, result.Refusal);
        }
        var body = Appeal(item);
        http.Response.Headers.ETag = body.ETag;
        return Results.Ok(body);
    }

    private static Task<IResult> AssignCaseAsync(Guid caseId, PhotoReviewAssignmentRequest request,
        HttpContext http, PhotoReviewService service, CancellationToken ct) => AssignAsync(caseId, request, http, service, false, ct);

    private static Task<IResult> AssignAppealAsync(Guid appealId, PhotoReviewAssignmentRequest request,
        HttpContext http, PhotoReviewService service, CancellationToken ct) => AssignAsync(appealId, request, http, service, true, ct);

    private static async Task<IResult> AssignAsync(Guid id, PhotoReviewAssignmentRequest request,
        HttpContext http, PhotoReviewService service, bool appeal, CancellationToken ct)
    {
        if (PhotoReviewHeaders.Command(http, appeal ? "appeal" : "case", id, out var version, out var key) is { } refused)
        {
            return refused;
        }
        if (request.ReviewerId == Guid.Empty)
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        var actor = CurrentSession.Require(http);
        var result = await service.AssignAsync(actor.UserId, actor.SessionId, id, request.ReviewerId, version, key, appeal, ct);
        if (result.Value is not { } value)
        {
            return Refused(http, result.Refusal);
        }
        var tag = PhotoReviewHeaders.Tag(appeal ? "appeal" : "case", id, value.Version);
        http.Response.Headers.ETag = tag;
        return appeal ? Results.Ok(new PhotoAppealMutationResponse(id, tag)) : Results.Ok(new PhotoReviewMutationResponse(id, tag));
    }

    private static Task<IResult> DecideCaseAsync(Guid caseId, PhotoReviewDecisionRequest request,
        HttpContext http, PhotoReviewService service, CancellationToken ct) => DecideAsync(caseId, request, http, service, false, ct);

    private static Task<IResult> DecideAppealAsync(Guid appealId, PhotoReviewDecisionRequest request,
        HttpContext http, PhotoReviewService service, CancellationToken ct) => DecideAsync(appealId, request, http, service, true, ct);

    private static async Task<IResult> DecideAsync(Guid id, PhotoReviewDecisionRequest request,
        HttpContext http, PhotoReviewService service, bool appeal, CancellationToken ct)
    {
        if (PhotoReviewHeaders.Command(http, appeal ? "appeal" : "case", id, out var version, out var key) is { } refused)
        {
            return refused;
        }
        var approve = request.Decision == "approve" && request.ReasonCode == "meets_policy";
        if (!approve && !(request.Decision == "reject" && request.ReasonCode == "does_not_meet_policy"))
        {
            return ApiProblems.Problem(400, ApiProblems.ValidationFailed);
        }
        var actor = CurrentSession.Require(http);
        var result = await service.DecideAsync(actor.UserId, actor.SessionId, id, version, key, appeal, approve, ct);
        if (result.Value is not { } value)
        {
            return Refused(http, result.Refusal);
        }
        var tag = PhotoReviewHeaders.Tag(appeal ? "appeal" : "case", id, value.Version);
        http.Response.Headers.ETag = tag;
        return Results.Ok(new PhotoReviewDecisionResponse(value.CaseId, value.AppealId, value.Decision!, tag,
            PhotoReviewHeaders.Tag("photo", value.PhotoId, value.PhotoVersion), value.AssetId));
    }

    private static Task<IResult> CaseEvidenceAsync(Guid caseId, string variant, HttpContext http,
        PhotoReviewService service, CancellationToken ct) => EvidenceAsync(caseId, variant, http, service, false, ct);

    private static Task<IResult> AppealEvidenceAsync(Guid appealId, string variant, HttpContext http,
        PhotoReviewService service, CancellationToken ct) => EvidenceAsync(appealId, variant, http, service, true, ct);

    private static async Task<IResult> EvidenceAsync(Guid id, string variant, HttpContext http,
        PhotoReviewService service, bool appeal, CancellationToken ct)
    {
        var parsed = PhotoVariants.All.Where(k => PhotoVariants.Name(k) == variant).Cast<PhotoVariant?>().SingleOrDefault();
        if (parsed is not { } kind)
        {
            return ApiProblems.Problem(404, "NOT_FOUND");
        }
        var actor = CurrentSession.Require(http);
        var result = await service.EvidenceAsync(actor.UserId, actor.SessionId, id, kind, appeal, ct);
        return result.Value is { } delivery ? new PhotoReviewEvidenceResult(delivery) : Refused(http, result.Refusal);
    }

    private static PhotoReviewCaseResponse Case(ReviewCase item) => new(item.CaseId, item.State,
        PhotoReviewHeaders.Tag("case", item.CaseId, item.Version), item.AwaitingReviewAt, item.ReviewDeadline, item.PolicyVersion);

    private static PhotoAppealResponse Appeal(ReviewAppeal item) => new(item.AppealId, item.PhotoId, item.State,
        PhotoReviewHeaders.Tag("appeal", item.AppealId, item.Version), item.RequestedAt,
        item.ResponseTargetAt, item.MaximumRetentionUntil, item.ClosedAt);

    private static ProblemHttpResult Refused(HttpContext http, ReviewRefusal? refusal) => refusal switch
    {
        ReviewRefusal.Forbidden => ApiProblems.Problem(403, "PHOTO_REVIEW_FORBIDDEN"),
        ReviewRefusal.NotFound => ApiProblems.Problem(404, "NOT_FOUND"),
        ReviewRefusal.PreconditionFailed => ApiProblems.Problem(412, "PRECONDITION_FAILED"),
        ReviewRefusal.KeyReused => ApiProblems.Problem(409, ApiProblems.IdempotencyKeyReused),
        ReviewRefusal.StateConflict => ApiProblems.Problem(409, "PHOTO_REVIEW_STATE_CONFLICT"),
        ReviewRefusal.ReviewerUnavailable => ApiProblems.Problem(409, "PHOTO_REVIEW_REVIEWER_UNAVAILABLE"),
        ReviewRefusal.Unavailable => Unavailable(http),
        _ => throw new InvalidOperationException("A private review response needs a result or refusal."),
    };

    private static ProblemHttpResult Unavailable(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
        return ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable);
    }
}
