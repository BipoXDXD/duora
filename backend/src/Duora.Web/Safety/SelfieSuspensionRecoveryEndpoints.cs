using Duora.Contracts.Safety;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi;

namespace Duora.Web.Safety;

internal static class SelfieSuspensionRecoveryEndpoints
{
    internal static void MapSelfieSuspensionRecoveryEndpoints(this RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/selfie-suspension-recoveries/{deliveryId:guid}")
            .WithTags("AccountModeration").RequireAuthorization(AccountModerationPolicy.Name);
        Problems(group.MapGet("", DiagnoseAsync)).Produces<SelfieRecoveryDiagnosisResponse>();
        Problems(group.MapPost("", RecoverAsync)).RequireRecentMfa().Produces<SelfieRecoveryResponse>()
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Keep this key and the exact incident/rationale when retrying an uncertain response.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 64, Pattern = "^[A-Za-z0-9_-]+$" },
                });
                return Task.CompletedTask;
            });
    }

    private static RouteHandlerBuilder Problems(RouteHandlerBuilder route) => route
        .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static ModeratorSession Moderator(HttpContext http)
    {
        var (userId, sessionId) = CurrentSession.Require(http);
        return new(userId, sessionId);
    }

    private static async Task<IResult> DiagnoseAsync(Guid deliveryId, HttpContext http,
        AccountSuspensions suspensions, CancellationToken cancellationToken)
    {
        var diagnosis = await suspensions.DiagnoseSelfieRecoveryAsync(Moderator(http), deliveryId, cancellationToken);
        return diagnosis is { } d
            ? TypedResults.Ok(new SelfieRecoveryDiagnosisResponse(d.DeliveryId, d.EventId, d.VerificationId,
                d.SubjectUserId, d.Standing, d.ReviewedAt?.UtcDateTime, d.SuspensionId, d.SuspensionReason))
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");
    }

    private static async Task<IResult> RecoverAsync(Guid deliveryId, RecoverSelfieSuspensionRequest body,
        HttpContext http, AccountSuspensions suspensions, CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID");
        }
        SelfieRecoveryRequest request;
        try
        {
            request = new(deliveryId, body.IncidentReference!, body.InternalReason!, key.Value);
        }
        catch (ArgumentException error)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                [error.ParamName ?? "request"] = [RequestValidation.InvalidFormat],
            });
        }
        var outcome = await suspensions.SuspendAsModeratorAsync(Moderator(http), request, cancellationToken);
        if (outcome.Result is { } result)
        {
            return TypedResults.Ok(new SelfieRecoveryResponse(result.DeliveryId, result.Disposition,
                result.SuspensionId, result.RecordedAt.UtcDateTime, result.Replayed));
        }
        return outcome.Code switch
        {
            "Forbidden" => ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountModerationForbidden),
            "NotFound" => ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND"),
            "KeyReused" => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused),
            "Busy" => Busy(http),
            "LinkMissing" => Conflict("SELFIE_RECOVERY_LINK_MISSING"),
            "AlreadyReviewed" => Conflict("SELFIE_RECOVERY_ALREADY_REVIEWED"),
            "AwaitingOtherMeasure" => Conflict("SELFIE_RECOVERY_AWAITING_OTHER_MEASURE"),
            "NotSuspendable" => Conflict(ApiProblems.AccountNotSuspendable),
            _ => throw new InvalidOperationException("Unmapped selfie recovery outcome."),
        };
    }

    private static ProblemHttpResult Conflict(string code) => ApiProblems.Problem(StatusCodes.Status409Conflict, code);

    private static ProblemHttpResult Busy(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
    }
}
