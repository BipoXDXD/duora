using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>Presença e qualidade apenas. Nunca solicita atributos de idade ou comparação de identidade.</summary>
internal sealed class RekognitionSelfieProvider(
    IAmazonRekognition client, SelfieProviderOptions options, TimeProvider time) : ISelfieProvider
{
    public async Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var response = await client.CreateFaceLivenessSessionAsync(new CreateFaceLivenessSessionRequest
        {
            ClientRequestToken = attemptId.ToString("N"),
            Settings = new CreateFaceLivenessSessionRequestSettings { AuditImagesLimit = 0 },
        }, cancellationToken);
        if (!Guid.TryParse(response.SessionId, out _))
        {
            throw new InvalidDataException("Invalid provider session.");
        }

        return new SelfieSession(response.SessionId, startedAt.AddMinutes(3));
    }

    public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        var response = await client.GetFaceLivenessSessionResultsAsync(
            new GetFaceLivenessSessionResultsRequest { SessionId = reference }, cancellationToken);
        using var bytes = response.ReferenceImage?.Bytes;
        if (response.Status == LivenessSessionStatus.CREATED || response.Status == LivenessSessionStatus.IN_PROGRESS)
        {
            return Result(SelfieState.Processing, null);
        }

        // Sessão que expirou (abandono) ou falhou no fornecedor não julgou a captura: não conta como reprovação no
        // limite de tentativas (issue #300) e pede nova captura como qualquer tentativa expirada.
        if (response.Status == LivenessSessionStatus.EXPIRED || response.Status == LivenessSessionStatus.FAILED)
        {
            return Result(SelfieState.Expired, null);
        }

        // Sem confiança ou sem frame de referência utilizável a AWS recomenda repetir a verificação: falha técnica,
        // não julgamento da captura, então não conta no limite (issue #300).
        if (response.Status == LivenessSessionStatus.SUCCEEDED && response.Confidence is null)
        {
            return Result(SelfieState.Expired, null);
        }

        if (response.Status != LivenessSessionStatus.SUCCEEDED || response.Confidence is not { } confidence
            || !options.AcceptsLiveness(confidence))
        {
            return Result(SelfieState.Inconclusive, "PRESENCE_INCONCLUSIVE");
        }

        if (bytes is null || bytes.Length == 0 || bytes.Length > MaximumReferenceFrameBytes)
        {
            return Result(SelfieState.Expired, null);
        }

        var faces = await client.DetectFacesAsync(new DetectFacesRequest
        {
            Attributes = ["DEFAULT"],
            Image = new Image { Bytes = bytes },
        }, cancellationToken);
        if (faces.FaceDetails is not { Count: 1 } || faces.FaceDetails[0] is not { Quality: { Brightness: { } brightness, Sharpness: { } sharpness }, Pose: { Pitch: { } pitch, Yaw: { } yaw, Roll: { } roll } })
        {
            return Result(SelfieState.RecaptureRequired, "CAPTURE_QUALITY");
        }

        var good = options.AcceptsFace(new SelfieFaceQuality(brightness, sharpness, pitch, yaw, roll));
        return good ? Result(SelfieState.Passed, null) : Result(SelfieState.RecaptureRequired, "CAPTURE_QUALITY");
    }

    /// <summary>Teto do DetectFaces para imagem em bytes (5 MB).</summary>
    private const int MaximumReferenceFrameBytes = 5 * 1024 * 1024;

    private SelfieResult Result(SelfieState state, string? feedback) => new(state, feedback, options.PolicyVersion);
}
