using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Media;

/// <summary>
/// API-M04: derivado aprovado transmitido pela própria API, nunca URL ou chave do storage (ADR-0010/0025, TM-44).
/// Sem cache (<c>no-store</c>), sem range e sem ETag: cada pedido relê a autorização (TM-25).
/// </summary>
internal static class MediaAssetEndpoints
{
    private const string VariantParameter = "variant";
    private const int CopyBufferBytes = 64 * 1024;

    public static void MapMediaAssetEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("/media").WithTags("Media")
            .MapGet("/{assetId:guid}", ReadAsync)
            .WithDescription(
                "Derivado de uma foto aprovada (thumb, card ou full; padrão full) como image/webp. Hoje só o titular lê; "
                + "asset de outra conta, inexistente, não aprovado ou excluído dão o mesmo 404. Cache-Control: no-store.")
            .Produces(StatusCodes.Status200OK, contentType: "image/webp")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireMediaAdmission()
            .AddOpenApiOperationTransformer(DocumentVariant);
    }

    private static async Task<IResult> ReadAsync(
        Guid assetId,
        HttpContext context,
        OwnerMediaReads reads,
        CancellationToken cancellationToken)
    {
        if (!TryReadVariant(context.Request, out var variant))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, ApiProblems.ValidationFailed);
        }

        var (userId, sessionId) = CurrentSession.Require(context);
        var media = await reads.OpenAsync(userId, sessionId, assetId, variant, cancellationToken);
        return media is null
            ? ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND")
            : new ApprovedMediaResult(media);
    }

    /// <summary>Ausente vale <c>full</c>; repetido, vazio ou fora da lista é 400, sem tentar adivinhar.</summary>
    private static bool TryReadVariant(HttpRequest request, out PhotoVariant variant)
    {
        variant = PhotoVariant.Full;
        if (!request.Query.TryGetValue(VariantParameter, out var values))
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var match = PhotoVariants.All.Where(kind => PhotoVariants.Name(kind) == values[0]).Cast<PhotoVariant?>().SingleOrDefault();
        variant = match ?? PhotoVariant.Full;
        return match is not null;
    }

    private static Task DocumentVariant(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Default = System.Text.Json.Nodes.JsonValue.Create("full") };
        schema.Enum = [.. PhotoVariants.All.Select(kind => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(PhotoVariants.Name(kind))!)];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = VariantParameter,
            In = ParameterLocation.Query,
            Required = false,
            Description = "Derivative size: thumb, card or full (default).",
            Schema = schema,
        });
        return Task.CompletedTask;
    }

    private sealed class ApprovedMediaResult(AuthorizedMedia media) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using var owned = media;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers.ContentDisposition = "inline";
            http.Response.ContentType = "image/webp";
            try
            {
                await owned.Content.CopyToAsync(http.Response.Body, CopyBufferBytes, http.RequestAborted);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
                {
                    http.Abort();
                    return;
                }

                http.Response.ContentType = null;
                ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
                await ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable).ExecuteAsync(http);
            }
        }
    }
}
