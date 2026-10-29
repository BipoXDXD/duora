using System.Globalization;
using Duora.Contracts.Safety;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Safety;

/// <summary>
/// Moderação de conta pela administração (issue #299, ADR-0042): ler o estado, suspender por violação dos Termos e
/// revogar. O grupo <c>/admin</c> já exige sessão administrativa com MFA; os comandos pedem também TOTP recente
/// (ADR-0041), e o serviço confere ao vivo, na transação, o papel <c>moderator</c>, que é a permissão específica.
/// </summary>
internal static class AccountSuspensionEndpoints
{
    private const string TagKind = "suspension";

    internal static void MapAccountSuspensionAdminEndpoints(this RouteGroupBuilder admin)
    {
        var accounts = admin.MapGroup("/accounts/{userId:guid}").WithTags("AccountModeration");
        Problems(accounts.MapGet("/suspension", ReadAsync))
            .Produces<AccountModerationStateResponse>();
        Problems(accounts.MapPost("/suspensions", SuspendAsync))
            .RequireRecentMfa()
            .Produces<AccountSuspensionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .AddOpenApiOperationTransformer(DocumentIdempotencyKey);
        Problems(accounts.MapPost("/suspensions/{suspensionId:guid}/lift", LiftAsync))
            .RequireRecentMfa()
            .Produces<AccountSuspensionResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .AddOpenApiOperationTransformer(DocumentIfMatch);
    }

    private static RouteHandlerBuilder Problems(RouteHandlerBuilder route) => route
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> ReadAsync(
        Guid userId,
        HttpContext http,
        AccountSuspensions suspensions,
        CancellationToken cancellationToken)
    {
        var outcome = await suspensions.ReadAsModeratorAsync(Moderator(http), userId, cancellationToken);
        return outcome switch
        {
            SuspensionReadOutcome.Found found => TypedResults.Ok(new AccountModerationStateResponse(
                userId, found.Active is { } active ? Response(active) : null)),
            SuspensionReadOutcome.Forbidden => Forbidden(),
            SuspensionReadOutcome.NotFound => NotFound(),
            _ => throw new InvalidOperationException("Leitura de moderação sem resultado mapeado."),
        };
    }

    private static async Task<IResult> SuspendAsync(
        Guid userId,
        SuspendAccountRequest request,
        HttpContext http,
        AccountSuspensions suspensions,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID");
        }

        var moderator = Moderator(http);
        if (request.ReasonCode != nameof(SuspensionReason.TermsViolation))
        {
            return Invalid("reasonCode");
        }

        if (request.InternalReason is not { } reason
            || Parse(() => SuspensionOrder.ByModerator(moderator.UserId, userId, SuspensionReason.TermsViolation, reason))
                is not { } order)
        {
            return Invalid("internalReason");
        }

        var outcome = await suspensions.SuspendAsModeratorAsync(
            moderator, order, ModerationCommandKey.ForModerator(moderator.UserId, key.Value), cancellationToken);
        return outcome switch
        {
            SuspensionOutcome.Suspended suspended => Created(http, suspended.View),
            SuspensionOutcome.AlreadySuspended => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.AccountAlreadySuspended),
            SuspensionOutcome.KeyReused => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused),
            SuspensionOutcome.NotSuspendable => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.AccountNotSuspendable),
            SuspensionOutcome.NotFound => NotFound(),
            SuspensionOutcome.Forbidden => Forbidden(),
            SuspensionOutcome.Busy => Busy(http),
            _ => throw new InvalidOperationException("Suspensão sem resultado mapeado."),
        };
    }

    private static async Task<IResult> LiftAsync(
        Guid userId,
        Guid suspensionId,
        LiftSuspensionRequest request,
        HttpContext http,
        AccountSuspensions suspensions,
        CancellationToken cancellationToken)
    {
        if (ExpectedVersion(http, suspensionId) is not { } version)
        {
            return string.IsNullOrWhiteSpace(http.Request.Headers.IfMatch.ToString())
                ? ApiProblems.Problem(StatusCodes.Status428PreconditionRequired, ApiProblems.PreconditionRequired)
                : ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed);
        }

        var moderator = Moderator(http);
        if (request.InternalReason is not { } reason
            || Parse(() => SuspensionLift.ByModerator(moderator.UserId, reason)) is not { } lift)
        {
            return Invalid("internalReason");
        }

        var outcome = await suspensions.LiftAsModeratorAsync(moderator, userId, suspensionId, version, lift, cancellationToken);
        switch (outcome)
        {
            case LiftOutcome.Lifted lifted:
                var body = Response(lifted.View);
                http.Response.Headers.ETag = body.ETag;
                return TypedResults.Ok(body);
            case LiftOutcome.PreconditionFailed:
                return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed);
            case LiftOutcome.NotFound:
                return NotFound();
            case LiftOutcome.Forbidden:
                return Forbidden();
            case LiftOutcome.Busy:
                return Busy(http);
            default:
                throw new InvalidOperationException("Revogação sem resultado mapeado.");
        }
    }

    private static ModeratorSession Moderator(HttpContext http)
    {
        var (userId, sessionId) = CurrentSession.Require(http);
        return new ModeratorSession(userId, sessionId);
    }

    /// <summary>A fronteira converte o corpo no pedido de domínio; texto inválido vira 400, nunca 500.</summary>
    private static T? Parse<T>(Func<T> parse)
        where T : class
    {
        try
        {
            return parse();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Só a tag exata desta medida vale; a de outra medida, ou malformada, não casa.</summary>
    private static long? ExpectedVersion(HttpContext http, Guid suspensionId)
    {
        var match = http.Request.Headers.IfMatch.ToString();
        var prefix = $"\"{TagKind}-{suspensionId:N}-";
        if (!match.StartsWith(prefix, StringComparison.Ordinal) || !match.EndsWith('"')
            || !long.TryParse(match.AsSpan(prefix.Length, match.Length - prefix.Length - 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version <= 0 || PhotoReviewHeaders.Tag(TagKind, suspensionId, version) != match)
        {
            return null;
        }

        return version;
    }

    private static AccountSuspensionResponse Response(SuspensionView view) => new(
        view.Id,
        view.SubjectUserId,
        view.Reason.ToString(),
        view.DecidedAutomatically,
        view.SuspendedAt.UtcDateTime,
        view.LiftedAt?.UtcDateTime,
        PhotoReviewHeaders.Tag(TagKind, view.Id, view.Version));

    private static Created<AccountSuspensionResponse> Created(HttpContext http, SuspensionView view)
    {
        var body = Response(view);
        http.Response.Headers.ETag = body.ETag;
        return TypedResults.Created($"/api/v1/admin/accounts/{view.SubjectUserId:D}/suspension", body);
    }

    private static ValidationProblem Invalid(string field) =>
        ApiProblems.Validation(new Dictionary<string, string[]> { [field] = [RequestValidation.InvalidFormat] });

    private static ProblemHttpResult Forbidden() =>
        ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountModerationForbidden);

    private static ProblemHttpResult NotFound() => ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");

    private static ProblemHttpResult Busy(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
    }

    private static Task DocumentIdempotencyKey(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "A stable key for this exact suspension request; a replay returns the same suspension.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 64, Pattern = "^[A-Za-z0-9_-]+$" },
        });
        return Task.CompletedTask;
    }

    private static Task DocumentIfMatch(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "If-Match",
            In = ParameterLocation.Header,
            Required = true,
            Description = "The exact strong ETag of this suspension, from the last read.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        return Task.CompletedTask;
    }
}
