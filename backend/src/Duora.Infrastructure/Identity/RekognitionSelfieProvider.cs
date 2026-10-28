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

        if (response.Status != LivenessSessionStatus.SUCCEEDED || response.Confidence is not { } confidence
            || !options.AcceptsLiveness(confidence))
        {
            return Result(SelfieState.Inconclusive, "PRESENCE_INCONCLUSIVE");
        }

        if (bytes is null || bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024)
        {
            return Result(SelfieState.RecaptureRequired, "CAPTURE_QUALITY");
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

    private SelfieResult Result(SelfieState state, string? feedback) => new(state, feedback, options.PolicyVersion);
}
