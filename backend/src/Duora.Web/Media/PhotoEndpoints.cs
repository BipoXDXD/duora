using System.Diagnostics.CodeAnalysis;
using Duora.Contracts.Media;
using Duora.Core.Media;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Media;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Safety;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.OpenApi;

namespace Duora.Web.Media;

/// <summary>
/// Fotos do titular: envio para a quarentena (API-M01) e estado do processamento (API-M02), ADR-0025. O alvo é
/// sempre a conta da sessão; foto de outra conta e foto inexistente dão o mesmo 404. O Web não decodifica imagem:
/// só confere os headers, transmite e calcula o hash.
/// </summary>
internal static class PhotoEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string PhotoTagKind = "photo";

    private static readonly TimeSpan InProgressRetryAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingLimitRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueFullRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StorageRetryAfter = TimeSpan.FromSeconds(30);

    public static void MapPhotoEndpoints(this IEndpointRouteBuilder api)
    {
        var photos = api.MapGroup("/me/photos").WithTags("Photos");

        photos.MapPost("", UploadAsync)
            .WithDescription(
                "Envia uma foto em corpo cru (image/jpeg, image/png ou image/webp), com Content-Length de 1 byte a "
                + "5 MiB e Idempotency-Key obrigatórios. Responde 202 com a foto em quarentena; a mesma chave com os "
                + "mesmos bytes devolve a mesma foto, e com outros bytes, 409.")
            .Accepts<Stream>(PhotoFormats.ContentType(PhotoFormat.Jpeg), PhotoFormats.ContentType(PhotoFormat.Png), PhotoFormats.ContentType(PhotoFormat.WebP))
            .Produces<PhotoUploadResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status411LengthRequired)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(DocumentUploadRequest);
        photos.MapGet("/{photoId:guid}/status", GetStatusAsync)
            .WithDescription(
                "Estado do processamento de uma foto do titular. O header ETag traz a versão forte e opaca da foto, "
                + "a única aceita no If-Match da exclusão.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentPhotoETag);
        photos.MapDelete("/{photoId:guid}", DeleteAsync)
            .WithDescription(
                "Exclusão lógica imediata da própria foto (API-M03), sem corpo, com If-Match (ETag do status) e "
                + "Idempotency-Key obrigatórios. Responde 204 com o ETag da foto excluída; a mesma chave repetida devolve "
                + "o mesmo resultado por 1 ano. Depois disso o recibo sai pela retenção, e repetir o DELETE com a mesma "
                + "chave devolve 412, sem novo efeito. Foto de outra conta e foto inexistente dão o mesmo 404. A remoção "
                + "dos arquivos é feita depois, por um job durável.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequireMediaAdmission()
            .AddOpenApiOperationTransformer(DocumentDeleteHeaders);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        AccountService accounts,
        PhotoUploads uploads,
        PhotoUploadLimiter limiter,
        CancellationToken cancellationToken)
    {
        if (!uploads.UploadsEnabled)
        {
            return ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.MediaUploadsDisabled);
        }

        if (!TryReadHeaders(context.Request, idempotencyKey, out var command, out var refusal))
        {
            return refusal;
        }

        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || await accounts.FindAccountAsync(userId, cancellationToken) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        if (!PhotoUploadPolicy.Allows(account.Status))
        {
            return ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                ApiProblems.AccountRestricted);
        }

        if (limiter.TryConsume(userId, context.Connection.RemoteIpAddress) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }

        AllowPhotoSizedBody(context, uploads);
        var result = await uploads.ReceiveAsync(command with { OwnerId = userId }, context.Request.Body, cancellationToken);
        return Respond(context, result);
    }

    /// <summary>
    /// Tudo o que dá para recusar sem ler o corpo: tipo, tamanho e chave. O teto vem do <c>Content-Length</c>,
    /// antes de o Kestrel começar a ler (spec 16.1).
    /// </summary>
    private static bool TryReadHeaders(
        HttpRequest request,
        string? idempotencyKey,
        [NotNullWhen(true)] out PhotoUploadCommand? command,
        [NotNullWhen(false)] out IResult? refusal)
    {
        command = null;
        refusal = RefuseHeaders(request, idempotencyKey);
        if (refusal is not null
            || !PhotoFormats.TryParseContentType(request.ContentType, out var format)
            || request.ContentLength is not { } length
            || !IdempotencyKey.TryParse(idempotencyKey, out var key))
        {
            refusal ??= TypedResults.BadRequest();
            return false;
        }

        command = new PhotoUploadCommand(Guid.Empty, key, format, length);
        return true;
    }

    private static IResult? RefuseHeaders(HttpRequest request, string? idempotencyKey)
    {
        if (!PhotoFormats.TryParseContentType(request.ContentType, out _))
        {
            return ApiProblems.Problem(
                StatusCodes.Status415UnsupportedMediaType,
                ApiProblems.UnsupportedMediaType);
        }

        switch (PhotoLimits.CheckContentLength(request.ContentLength))
        {
            case UploadLengthVerdict.Missing:
                return ApiProblems.Problem(
                    StatusCodes.Status411LengthRequired,
                    ApiProblems.LengthRequired);
            case UploadLengthVerdict.TooLarge:
                return ApiProblems.Problem(
                    StatusCodes.Status413PayloadTooLarge,
                    ApiProblems.PayloadTooLarge);
            case UploadLengthVerdict.Empty:
                return UploadValidationFormat.Refuse(request, "body", "A foto está vazia.", "PHOTO_BODY_EMPTY");
            case UploadLengthVerdict.Accepted:
                break;
        }

        return IdempotencyKey.TryParse(idempotencyKey, out _)
            ? null
            : UploadValidationFormat.Refuse(
                request,
                IdempotencyKeyHeader,
                "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.",
                "IDEMPOTENCY_KEY_INVALID");
    }

    /// <summary>
    /// O teto global do Kestrel é o do JSON comum (32 KiB); só esta rota aceita até 5 MiB. A taxa mínima também é
    /// fixada aqui, de propósito, em vez de herdar o padrão do servidor: cliente lento é cortado e a reserva desfeita.
    /// </summary>
    private static void AllowPhotoSizedBody(HttpContext context, PhotoUploads uploads)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = PhotoLimits.MaxBytes;
        }

        if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = new MinDataRate(uploads.MinUploadBytesPerSecond, uploads.MinUploadRateGracePeriod);
        }
    }

    private static IResult Respond(HttpContext context, PhotoUploadResult result) => result switch
    {
        PhotoUploadResult.Accepted accepted => TypedResults.Accepted(
            $"/api/v1/me/photos/{accepted.PhotoId}/status",
            new PhotoUploadResponse(accepted.PhotoId, accepted.State.ToString())),
        PhotoUploadResult.InProgress => RetryLater(
            context,
            InProgressRetryAfter,
            StatusCodes.Status409Conflict,
            ApiProblems.IdempotencyRequestInProgress),
        PhotoUploadResult.KeyReused => ApiProblems.Problem(
            StatusCodes.Status409Conflict,
            ApiProblems.IdempotencyKeyReused),
        PhotoUploadResult.PendingLimitReached => RetryLater(
            context,
            PendingLimitRetryAfter,
            StatusCodes.Status429TooManyRequests,
            ApiProblems.UploadPendingLimit),
        PhotoUploadResult.QueueFull => RetryLater(
            context,
            QueueFullRetryAfter,
            StatusCodes.Status503ServiceUnavailable,
            ApiProblems.MediaQueueFull),
        PhotoUploadResult.OwnerMissing => TypedResults.Unauthorized(),
        PhotoUploadResult.AccountRestricted => ApiProblems.Problem(
            StatusCodes.Status403Forbidden,
            ApiProblems.AccountRestricted),
        PhotoUploadResult.BodyIncomplete => ApiProblems.Problem(
            StatusCodes.Status400BadRequest,
            ApiProblems.UploadIncomplete),
        PhotoUploadResult.StorageFailed => RetryLater(
            context,
            StorageRetryAfter,
            StatusCodes.Status503ServiceUnavailable,
            ApiProblems.StorageUnavailable),
        _ => throw new InvalidOperationException("Resultado de upload sem resposta."),
    };

    private static ProblemHttpResult RetryLater(HttpContext context, TimeSpan retryAfter, int status, string code)
    {
        ApiProblems.SetRetryAfter(context, retryAfter);
        return ApiProblems.Problem(status, code);
    }

    private static async Task<Results<Ok<PhotoStatusResponse>, NotFound, UnauthorizedHttpResult>> GetStatusAsync(
        Guid photoId,
        HttpContext context,
        PhotoUploads uploads,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (await uploads.FindOwnStatusAsync(userId, photoId, cancellationToken) is not { } status)
        {
            return TypedResults.NotFound();
        }

        context.Response.Headers.ETag = PhotoReviewHeaders.Tag(PhotoTagKind, status.PhotoId, status.Version);
        return TypedResults.Ok(new PhotoStatusResponse(status.PhotoId, status.State.ToString(), status.RejectionCode));
    }

    private static async Task<IResult> DeleteAsync(
        Guid photoId,
        HttpContext context,
        PhotoDeletions deletions,
        CancellationToken cancellationToken)
    {
        if (HasBody(context))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, ApiProblems.ValidationFailed);
        }

        if (PhotoReviewHeaders.Command(context, PhotoTagKind, photoId, out var version, out var key) is { } refused)
        {
            return refused;
        }

        var (userId, sessionId) = CurrentSession.Require(context);
        var result = await deletions.DeleteAsync(userId, sessionId, photoId, version, key, cancellationToken);
        switch (result)
        {
            case PhotoDeletionResult.Deleted deleted:
                context.Response.Headers.ETag = PhotoReviewHeaders.Tag(PhotoTagKind, photoId, deleted.PhotoVersion);
                return TypedResults.NoContent();
            case PhotoDeletionResult.Forbidden:
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN");
            case PhotoDeletionResult.NotFound:
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND");
            case PhotoDeletionResult.PreconditionFailed:
                return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed);
            case PhotoDeletionResult.KeyReused:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused);
            case PhotoDeletionResult.StateConflict:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.PhotoDeleteStateConflict);
            default:
                throw new InvalidOperationException("Resultado de exclusão sem resposta.");
        }
    }

    /// <summary>DELETE não tem corpo neste contrato: qualquer conteúdo é recusado antes de tocar no banco.</summary>
    private static bool HasBody(HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
        || context.Request.ContentLength is > 0
        || context.Request.Headers.TransferEncoding.Count != 0;

    private static Task DocumentPhotoETag(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses?["200"] is OpenApiResponse ok)
        {
            ok.Headers ??= new Dictionary<string, IOpenApiHeader>();
            ok.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Strong opaque version of this photo, for If-Match on DELETE.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        return Task.CompletedTask;
    }

    private static Task DocumentDeleteHeaders(
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
            Description = "The exact strong ETag of this photo from the status read; weak tags, lists, wildcards and tags of other resources never match.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyKeyHeader,
            In = ParameterLocation.Header,
            Required = true,
            Description = "A stable key for this exact deletion. Authenticated replay returns the confirmed result.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 1,
                MaxLength = IdempotencyKey.MaxLength,
                Pattern = "^[A-Za-z0-9_-]+$",
            },
        });
        if (operation.Responses?["204"] is OpenApiResponse deleted)
        {
            deleted.Headers ??= new Dictionary<string, IOpenApiHeader>();
            deleted.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Strong opaque version of the deleted photo.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>Corpo binário e Idempotency-Key obrigatório no contrato, como a rota exige.</summary>
    private static Task DocumentUploadRequest(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter { Name: IdempotencyKeyHeader } header)
            {
                header.Required = true;
                header.Description = "Chave do cliente para repetir o envio sem duplicar: 1 a 64 caracteres [A-Za-z0-9_-].";
                header.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    MinLength = 1,
                    MaxLength = IdempotencyKey.MaxLength,
                    Pattern = "^[A-Za-z0-9_-]+$",
                };
            }
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = UploadValidationFormat.Header,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Optional request-local validation format. Only one exact codes-v1 value selects field codes for empty body or invalid Idempotency-Key; all other values preserve the legacy strings. Accept-Language does not select this format.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        if (operation.Responses?["400"] is OpenApiResponse validation)
        {
            validation.Headers ??= new Dictionary<string, IOpenApiHeader>();
            validation.Headers[UploadValidationFormat.Header] = new OpenApiHeader
            {
                Description = "Present as codes-v1 only for the two negotiated VALIDATION_FAILED refusals. Clients must require this exact confirmation before interpreting field codes; otherwise use local fallback without retrying POST.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^codes-v1$" },
            };
            validation.Headers["Vary"] = new OpenApiHeader
            {
                Description = "Includes Duora-Validation-Format for both representations of the two validation refusals, preserving existing values or wildcard. Does not make POST cacheable.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        if (operation.RequestBody is OpenApiRequestBody body)
        {
            body.Required = true;
            foreach (var media in body.Content?.Values ?? [])
            {
                media.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", MaxLength = null };
            }
        }

        return Task.CompletedTask;
    }
}
