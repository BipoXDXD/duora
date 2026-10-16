using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Duora.Web.Webhooks;

/// <summary>
/// Callbacks de fornecedor em <c>/webhooks/{provider}</c> (spec 8.5 e 16.4; ADR-0024). Rota pública por exceção
/// explícita: sem cookie, sem CSRF, autenticada só pela assinatura do fornecedor sobre os bytes recebidos.
/// </summary>
internal static class WebhookEndpoints
{
    /// <summary>Folga para ler um byte além do teto e saber que o corpo passou dele.</summary>
    private const int ReadBufferBytes = 8 * 1024;

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{provider}", ReceiveAsync)
            .AllowAnonymous()
            .WithMetadata(new CsrfExemptEndpoint())
            // O teto global de 32 KiB (spec 4.3) é menor que o de webhook (64 KiB, model.md).
            .WithMetadata(new RequestSizeLimitAttribute(AgeVerificationWebhook.MaxBodyBytes))
            .ExcludeFromDescription();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ReceiveAsync(
        string provider,
        HttpRequest request,
        AgeVerificationWebhook ageVerification,
        CancellationToken cancellationToken)
    {
        if (!ageVerification.Handles(provider))
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "Recurso não encontrado.");
        }

        if (await ReadBoundedAsync(request.Body, AgeVerificationWebhook.MaxBodyBytes, cancellationToken) is not { } body)
        {
            return ApiProblems.Problem(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Corpo grande demais.");
        }

        var signature = request.Headers[AgeVerificationWebhook.SignatureHeader];
        var outcome = await ageVerification.ProcessAsync(
            body,
            signature.Count == 1 ? signature.ToString() : null,
            cancellationToken);
        return outcome switch
        {
            AgeWebhookOutcome.Accepted => TypedResults.NoContent(),
            AgeWebhookOutcome.InvalidSignature => ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiProblems.WebhookSignatureInvalid,
                "Assinatura inválida."),
            AgeWebhookOutcome.InvalidPayload => ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiProblems.WebhookPayloadInvalid,
                "Corpo inválido."),
            _ => throw new InvalidOperationException($"Resultado de webhook sem resposta: {outcome}."),
        };
    }

    /// <summary>Lê os bytes crus até o teto; nulo se passar dele (a assinatura é sobre estes bytes, spec 16.4).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferBytes];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
