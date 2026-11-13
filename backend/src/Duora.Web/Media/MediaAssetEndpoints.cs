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

    /// <summary>
    /// Teto de cada escrita do corpo. Um derivado promovido já está inteiro em memória; escrito de uma vez, o Kestrel
    /// seguraria outra cópia inteira no pipe enquanto o cliente baixa.
    /// </summary>
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

    // A leitura do derivado falhou (I/O ou cancelamento, em geral do storage) antes do primeiro byte. Sem a exceção
    // do Npgsql a causa não se distingue, então o prazo é próprio da entrega.
    private static readonly TimeSpan StreamFailureRetryAfter = TimeSpan.FromSeconds(2);

    internal sealed class ApprovedMediaResult(AuthorizedMedia media) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using var owned = media;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers.ContentDisposition = "inline";
            http.Response.ContentType = "image/webp";
            // O prazo da reserva corta um cliente lento que seguraria o orçamento de memória dos outros donos.
            using var copy = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, owned.Expired);
            try
            {
                await CopyInChunksAsync(owned.Content, http.Response.Body, copy.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
                {
                    http.Abort();
                    return;
                }

                http.Response.ContentType = null;
                ApiProblems.SetRetryAfter(http, StreamFailureRetryAfter);
                await ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable).ExecuteAsync(http);
            }
        }

        /// <summary>
        /// <see cref="MemoryStream.CopyToAsync(Stream, int, CancellationToken)"/> ignora o tamanho pedido e escreve o
        /// resto do buffer de uma vez. A cópia aqui lê e escreve no máximo <see cref="CopyBufferBytes"/> por vez, com
        /// flush por fatia, seja qual for o stream de origem.
        /// </summary>
        private static async Task CopyInChunksAsync(Stream content, Stream body, CancellationToken cancellationToken)
        {
            var chunk = new byte[CopyBufferBytes];
            var read = await content.ReadAsync(chunk, cancellationToken);
            while (read > 0)
            {
                await body.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                await body.FlushAsync(cancellationToken);
                read = await content.ReadAsync(chunk, cancellationToken);
            }
        }
    }
}
