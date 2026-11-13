using Duora.Contracts.Safety;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Duora.Web.Safety;

/// <summary>
/// Reconciliação de inscrições legadas (issue #321). Sessão administrativa do grupo <c>/admin</c>, papel
/// <c>moderator</c> lido ao vivo pela policy (antes de qualquer parâmetro) e TOTP recente. Na prévia (API-ADM25) a
/// sessão é lida sem toque (ADR-0048) e o leitor revalida a moderação no próprio snapshot READ ONLY; os runs e as
/// unidades (API-ADM26 a API-ADM29, ADR-0049) usam a sessão normal, com toque. O corte vem só da configuração do servidor.
/// </summary>
internal static class RegistrationReconciliationEndpoints
{
    private const string CursorField = "cursor";
    private const string RunTagKind = "reconciliation-run";
    private const string DigestPattern = "^[0-9a-f]{64}$";
    private const string SingleLinePattern = "^[^\\u0000-\\u001F\\u007F-\\u009F]+$";

    /// <summary>Espera sugerida quando o snapshot passa do statement_timeout; a consulta é cara, não insistir já.</summary>
    private static readonly TimeSpan TimeoutRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Publica os limites reais do domínio, que os tipos do contrato não carregam.</summary>
    public static void ConfigureOpenApi(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            if (type == typeof(CreateRegistrationReconciliationRunRequest) && schema.Properties is { } request)
            {
                SetUnitsLimit(request["units"]);
                SetTextLimit(request["justification"], ReconciliationRunOrder.JustificationMaxLength);
                SetTextLimit(request["authorizationReference"], ReconciliationRunOrder.AuthorizationReferenceMaxLength);
                schema.Required = new HashSet<string> { "units", "justification", "authorizationReference" };
            }
            else if (type == typeof(ReconciliationRunUnitRequest) && schema.Properties is { } unit)
            {
                SetDigestShape(unit["unitDigest"]);
                schema.Required = new HashSet<string> { "subjectUserId", "originalMeasureId", "unitDigest" };
            }
            else if (type == typeof(ReconciliationPreviewUnitResponse) && schema.Properties is { } preview)
            {
                SetDigestShape(preview["unitDigest"]);
            }

            return Task.CompletedTask;
        });

    private static void SetUnitsLimit(IOpenApiSchema property)
    {
        if (property is OpenApiSchema units)
        {
            units.Type = JsonSchemaType.Array;
            units.MinItems = 1;
            units.MaxItems = ReconciliationRunOrder.MaxUnits;
        }
    }

    private static void SetTextLimit(IOpenApiSchema property, int maxLength)
    {
        if (property is OpenApiSchema text)
        {
            text.Type = JsonSchemaType.String;
            text.MinLength = 1;
            text.MaxLength = maxLength;
            text.Pattern = SingleLinePattern;
        }
    }

    private static void SetDigestShape(IOpenApiSchema property)
    {
        if (property is OpenApiSchema digest)
        {
            digest.Type = JsonSchemaType.String;
            digest.MinLength = ReconciliationUnitDigest.Length;
            digest.MaxLength = ReconciliationUnitDigest.Length;
            digest.Pattern = DigestPattern;
        }
    }

    internal static void MapRegistrationReconciliationAdminEndpoints(this RouteGroupBuilder admin)
    {
        // Permissão primeiro: a policy roda antes do binding, e quem não modera recebe 403 antes de qualquer validação.
        var reconciliations = admin.MapGroup("/registration-reconciliations").WithTags("RegistrationReconciliation")
            .RequireAuthorization(AccountModerationPolicy.Name);
        reconciliations.MapGet("/preview", PreviewAsync)
            .RequireRecentMfa()
            .WithReadOnlySession()
            .Produces<RegistrationReconciliationPreviewResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        reconciliations.MapPost("/runs", CreateRunAsync)
            .RequireRecentMfa()
            .Produces<RegistrationReconciliationRunResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(DocumentIdempotencyKey);
        reconciliations.MapGet("/runs/{runId:guid}", ReadRunAsync)
            .RequireRecentMfa()
            .Produces<RegistrationReconciliationRunResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        reconciliations.MapPost("/runs/{runId:guid}/units/{unitId:guid}:apply", ApplyUnitAsync)
            .RequireRecentMfa()
            .Produces<RegistrationReconciliationUnitReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(DocumentIdempotencyKey);
        reconciliations.MapPost("/runs/{runId:guid}:close", CloseRunAsync)
            .RequireRecentMfa()
            .Produces<RegistrationReconciliationRunResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(DocumentIfMatch);
    }

    private static async Task<IResult> PreviewAsync(
        string? cursorSubject,
        string? cursorMeasure,
        HttpContext http,
        LegacyRegistrationReconciliationPreviews previews,
        IOptions<RegistrationReconciliationCutoverOptions> cutover,
        ReconciliationRequestLimiter limiter,
        CancellationToken cancellationToken)
    {
        var moderator = Moderator(http);
        // Depois da autorização e antes do cursor: pedido inválido também conta, e o 429 não lê nada do banco.
        if (limiter.TryConsume(moderator.UserId) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(http, retryAfter);
        }

        if (!TryReadCursor(cursorSubject, cursorMeasure, out var after))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { [CursorField] = [RequestValidation.InvalidFormat] });
        }

        var evidence = cutover.Value.Evidence();
        var outcome = await previews.ReadAsModeratorAsync(moderator, evidence, after, cancellationToken);
        switch (outcome)
        {
            case ReconciliationPreviewOutcome.Ready ready:
                return TypedResults.Ok(Response(ready.Preview));
            case ReconciliationPreviewOutcome.Forbidden:
                return Forbidden();
            case ReconciliationPreviewOutcome.TimedOut:
                return TimedOut(http);
            default:
                throw new InvalidOperationException("Prévia sem resultado mapeado.");
        }
    }

    /// <summary>
    /// Cria o run (ADR-0049, D1): a sessão é a normal, com toque, e o corte vem só da configuração. O serviço revalida
    /// a moderação na transação e recalcula os digests; divergência é 412 sem gravar nada.
    /// </summary>
    private static async Task<IResult> CreateRunAsync(
        CreateRegistrationReconciliationRunRequest request,
        HttpContext http,
        RegistrationReconciliationRuns runs,
        IOptions<RegistrationReconciliationCutoverOptions> cutover,
        ReconciliationRequestLimiter limiter,
        CancellationToken cancellationToken)
    {
        var moderator = Moderator(http);
        // Mesma cota da prévia e no mesmo ponto: depois da autorização e do binding, e chave ou campo inválido contam.
        if (limiter.TryConsume(moderator.UserId) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(http, retryAfter);
        }

        if (!IdempotencyKey.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID");
        }

        ReconciliationRunOrder order;
        try
        {
            order = ReconciliationRunOrder.Create(
                request.Units?.Select(UnitRef).ToArray(), request.Justification, request.AuthorizationReference);
        }
        catch (ArgumentException refused)
        {
            // O ParamName é o nome do campo no corpo; a mensagem e o valor recebido nunca saem na resposta.
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                [refused.ParamName ?? "body"] = [RequestValidation.InvalidFormat],
            });
        }

        var outcome = await runs.CreateAsModeratorAsync(moderator, order, cutover.Value.Evidence(), key, cancellationToken);
        switch (outcome)
        {
            case ReconciliationRunOutcome.Created created:
                var body = Response(created.View);
                http.Response.Headers.ETag = body.ETag;
                return TypedResults.Created($"/api/v1/admin/registration-reconciliations/runs/{body.Id:D}", body);
            case ReconciliationRunOutcome.ManifestChanged:
                return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed);
            case ReconciliationRunOutcome.AlreadyOpen:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.RunAlreadyOpen);
            case ReconciliationRunOutcome.KeyReused:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused);
            case ReconciliationRunOutcome.Forbidden:
                return Forbidden();
            case ReconciliationRunOutcome.Busy:
                return Busy(http);
            case ReconciliationRunOutcome.TimedOut:
                return TimedOut(http);
            default:
                throw new InvalidOperationException("Criação de run sem resultado mapeado.");
        }
    }

    private static async Task<IResult> ReadRunAsync(
        Guid runId,
        HttpContext http,
        RegistrationReconciliationRuns runs,
        CancellationToken cancellationToken)
    {
        var outcome = await runs.ReadAsModeratorAsync(Moderator(http), runId, cancellationToken);
        switch (outcome)
        {
            case ReconciliationRunReadOutcome.Found found:
                var body = Response(found.View);
                http.Response.Headers.ETag = body.ETag;
                return TypedResults.Ok(body);
            case ReconciliationRunReadOutcome.NotFound:
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");
            case ReconciliationRunReadOutcome.Forbidden:
                return Forbidden();
            case ReconciliationRunReadOutcome.Busy:
                return Busy(http);
            case ReconciliationRunReadOutcome.TimedOut:
                return TimedOut(http);
            default:
                throw new InvalidOperationException("Leitura de run sem resultado mapeado.");
        }
    }

    /// <summary>
    /// Aplica uma unidade (ADR-0049, D2): o corpo é vazio e o corte vem só da configuração. Unidade terminal devolve o
    /// recibo, que é a idempotência; a <c>Idempotency-Key</c> só detecta intento divergente.
    /// </summary>
    private static async Task<IResult> ApplyUnitAsync(
        Guid runId,
        Guid unitId,
        HttpContext http,
        RegistrationReconciliationCoordinator coordinator,
        IOptions<RegistrationReconciliationCutoverOptions> cutover,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID");
        }

        var outcome = await coordinator.ApplyAsModeratorAsync(Moderator(http), runId, unitId, cutover.Value.Evidence(), key, cancellationToken);
        switch (outcome)
        {
            case ReconciliationApplyOutcome.Receipt receipt:
                return TypedResults.Ok(Response(receipt.Value));
            case ReconciliationApplyOutcome.NotFound:
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");
            case ReconciliationApplyOutcome.Forbidden:
                return Forbidden();
            case ReconciliationApplyOutcome.RunClosed:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.RunClosed);
            case ReconciliationApplyOutcome.CutoverChanged:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.CutoverChanged);
            case ReconciliationApplyOutcome.KeyReused:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused);
            case ReconciliationApplyOutcome.Busy:
                return Busy(http);
            case ReconciliationApplyOutcome.TimedOut:
                return TimedOut(http);
            default:
                throw new InvalidOperationException("Aplicação de unidade sem resultado mapeado.");
        }
    }

    /// <summary>Fecha o run na versão do <c>If-Match</c> (ADR-0049, D2); depois disso nenhuma unidade nova começa.</summary>
    private static async Task<IResult> CloseRunAsync(
        Guid runId,
        HttpContext http,
        RegistrationReconciliationRuns runs,
        CancellationToken cancellationToken)
    {
        if (PhotoReviewHeaders.IfMatchVersion(http, RunTagKind, runId, out var version) is { } refused)
        {
            return refused;
        }

        var outcome = await runs.CloseAsModeratorAsync(Moderator(http), runId, version, cancellationToken);
        switch (outcome)
        {
            case ReconciliationRunCloseOutcome.Closed closed:
                var body = Response(closed.View);
                http.Response.Headers.ETag = body.ETag;
                return TypedResults.Ok(body);
            case ReconciliationRunCloseOutcome.NotFound:
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");
            case ReconciliationRunCloseOutcome.PreconditionFailed:
                return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed);
            case ReconciliationRunCloseOutcome.AlreadyClosed:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.RunClosed);
            case ReconciliationRunCloseOutcome.Forbidden:
                return Forbidden();
            case ReconciliationRunCloseOutcome.Busy:
                return Busy(http);
            case ReconciliationRunCloseOutcome.TimedOut:
                return TimedOut(http);
            default:
                throw new InvalidOperationException("Encerramento de run sem resultado mapeado.");
        }
    }

    private static ReconciliationRunUnitRef UnitRef(ReconciliationRunUnitRequest? unit) =>
        new(unit?.SubjectUserId ?? Guid.Empty, unit?.OriginalMeasureId ?? Guid.Empty, unit?.UnitDigest ?? string.Empty);

    private static ModeratorSession Moderator(HttpContext http)
    {
        var (userId, sessionId) = CurrentSession.Require(http);
        return new ModeratorSession(userId, sessionId);
    }

    private static ProblemHttpResult Forbidden() =>
        ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountModerationForbidden);

    /// <summary>Contenção curta de lock ou índice: nada foi gravado e o pedido pode ser repetido com a mesma chave.</summary>
    private static ProblemHttpResult Busy(HttpContext http) => DatabaseUnavailability.Contention(http);

    /// <summary>Só nestas rotas: o statement_timeout delas não passa a valer como indisponibilidade em outras.</summary>
    private static ProblemHttpResult TimedOut(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, TimeoutRetryAfter);
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
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
            Description = "The ETag of the run; without it the API answers 428.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        return Task.CompletedTask;
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
            Description = "A stable key for this exact request; a replay returns the recorded result.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 64, Pattern = "^[A-Za-z0-9_-]+$" },
        });
        return Task.CompletedTask;
    }

    /// <summary>Os dois campos juntos ou nenhum; cada um um UUID no formato canônico com hífens, diferente de zero.</summary>
    private static bool TryReadCursor(string? subject, string? measure, out ReconciliationPreviewCursor? cursor)
    {
        cursor = null;
        if (subject is null && measure is null)
        {
            return true;
        }

        if (!TryReadId(subject, out var subjectId) || !TryReadId(measure, out var measureId))
        {
            return false;
        }

        cursor = new ReconciliationPreviewCursor(subjectId, measureId);
        return true;
    }

    private static bool TryReadId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    private static RegistrationReconciliationPreviewResponse Response(ReconciliationPreview preview) => new(
        preview.AsOf.UtcDateTime,
        preview.SelectorVersion,
        preview.Evidence?.Id,
        [.. preview.RegistrationsByPrimaryIssue.OrderBy(entry => entry.Key)
            .Select(entry => new ReconciliationIssueCountResponse(entry.Key.ToString(), entry.Value))],
        preview.ActionableRegistrations,
        preview.HeldRegistrations,
        new ReconciliationUnitCountsResponse(preview.Units.Eligible, preview.Units.Blocked, preview.Units.Oversized),
        [.. preview.Page.Select(unit => new ReconciliationPreviewUnitResponse(
            unit.SubjectUserId,
            unit.OriginalMeasureId,
            unit.MeasureVersion,
            [.. unit.Items.Select(Response)],
            unit.UnitDigest))],
        preview.Next is { } next ? new ReconciliationPreviewCursorResponse(next.SubjectUserId, next.OriginalMeasureId) : null);

    private static ReconciliationPreviewItemResponse Response(ReconciliationPreviewItem item) => new(
        item.EventId, item.RegistrationVersion, item.State.ToString(), item.MaxReleasedSeats, item.MaxPromotions);

    private static RegistrationReconciliationRunResponse Response(ReconciliationRunView run) => new(
        run.Id,
        run.SelectorVersion,
        run.EvidenceId,
        run.CutoverLower.UtcDateTime,
        run.CutoverUpper.UtcDateTime,
        run.CreatedByUserId,
        run.Justification,
        run.AuthorizationReference,
        run.State,
        run.Version,
        run.CreatedAt.UtcDateTime,
        PhotoReviewHeaders.Tag(RunTagKind, run.Id, run.Version),
        [.. run.Units.Select(unit => new RegistrationReconciliationRunUnitResponse(
            unit.Id,
            unit.SubjectUserId,
            unit.OriginalMeasureId,
            unit.MeasureVersion,
            unit.State,
            [.. unit.Items.Select(Response)],
            unit.Receipt is { } receipt ? Response(receipt) : null))]);

    private static RegistrationReconciliationUnitReceiptResponse Response(ReconciliationUnitReceipt receipt) => new(
        receipt.RunId,
        receipt.UnitId,
        receipt.State.ToString(),
        receipt.Reason,
        receipt.FactIds,
        receipt.ExecutedByUserId,
        receipt.ExecutedAt.UtcDateTime,
        receipt.CorrelationId);
}
