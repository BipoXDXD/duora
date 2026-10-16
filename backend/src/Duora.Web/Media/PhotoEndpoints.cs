using System.Diagnostics.CodeAnalysis;
using Duora.Contracts.Media;
using Duora.Core.Media;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Media;
using Duora.Web.Http;
using Duora.Web.Identity;
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

    private static readonly TimeSpan InProgressRetryAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingLimitRetryAfter = TimeSpan.FromSeconds(60);
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
            .WithDescription("Estado do processamento de uma foto do titular.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
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
                ApiProblems.MediaUploadsDisabled,
                "O envio de fotos está desligado.");
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
                ApiProblems.AccountRestricted,
                "A situação da conta não permite enviar fotos.");
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
                ApiProblems.UnsupportedMediaType,
                "Envie a foto como image/jpeg, image/png ou image/webp.");
        }

        switch (PhotoLimits.CheckContentLength(request.ContentLength))
        {
            case UploadLengthVerdict.Missing:
                return ApiProblems.Problem(
                    StatusCodes.Status411LengthRequired,
                    ApiProblems.LengthRequired,
                    "Envie o header Content-Length com o tamanho da foto.");
            case UploadLengthVerdict.TooLarge:
                return ApiProblems.Problem(
                    StatusCodes.Status413PayloadTooLarge,
                    ApiProblems.PayloadTooLarge,
                    "A foto passa de 5 MiB.");
            case UploadLengthVerdict.Empty:
                return ApiProblems.Validation(new Dictionary<string, string[]> { ["body"] = ["A foto está vazia."] });
            case UploadLengthVerdict.Accepted:
                break;
        }

        return IdempotencyKey.TryParse(idempotencyKey, out _)
            ? null
            : ApiProblems.Validation(new Dictionary<string, string[]>
            {
                [IdempotencyKeyHeader] = ["Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'."],
            });
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
            ApiProblems.IdempotencyRequestInProgress,
            "O envio com esta chave ainda está em andamento."),
        PhotoUploadResult.KeyReused => ApiProblems.Problem(
            StatusCodes.Status409Conflict,
            ApiProblems.IdempotencyKeyReused,
            "Esta chave já foi usada com outra foto."),
        PhotoUploadResult.PendingLimitReached => RetryLater(
            context,
            PendingLimitRetryAfter,
            StatusCodes.Status429TooManyRequests,
            ApiProblems.UploadPendingLimit,
            "Há fotos demais em processamento. Aguarde o resultado delas."),
        PhotoUploadResult.OwnerMissing => TypedResults.Unauthorized(),
        PhotoUploadResult.AccountRestricted => ApiProblems.Problem(
            StatusCodes.Status403Forbidden,
            ApiProblems.AccountRestricted,
            "A situação da conta não permite enviar fotos."),
        PhotoUploadResult.BodyIncomplete => ApiProblems.Problem(
            StatusCodes.Status400BadRequest,
            ApiProblems.UploadIncomplete,
            "A foto não chegou inteira. Envie de novo com a mesma chave."),
        PhotoUploadResult.StorageFailed => RetryLater(
            context,
            StorageRetryAfter,
            StatusCodes.Status503ServiceUnavailable,
            ApiProblems.StorageUnavailable,
            "Não foi possível guardar a foto agora. Envie de novo com a mesma chave."),
        _ => throw new InvalidOperationException("Resultado de upload sem resposta."),
    };

    private static ProblemHttpResult RetryLater(HttpContext context, TimeSpan retryAfter, int status, string code, string title)
    {
        ApiProblems.SetRetryAfter(context, retryAfter);
        return ApiProblems.Problem(status, code, title);
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

        return await uploads.FindOwnStatusAsync(userId, photoId, cancellationToken) is { } status
            ? TypedResults.Ok(new PhotoStatusResponse(status.PhotoId, status.State.ToString(), status.RejectionCode))
            : TypedResults.NotFound();
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
