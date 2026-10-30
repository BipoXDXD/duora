using Amazon;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.Runtime;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.Identity;

public sealed class RekognitionSelfieProviderTests
{
    [Theory]
    [InlineData(99, 80, SelfieState.Passed)]
    [InlineData(40, 80, SelfieState.Inconclusive)]
    [InlineData(99, 10, SelfieState.RecaptureRequired)]
    public async Task PresenceAndQualityAreIndependentWithoutRequestingAge(float liveness, float brightness, SelfieState expected)
    {
        using var client = new Client(liveness, brightness);
        var options = new SelfieProviderOptions
        {
            PolicyVersion = "test",
            MinimumLiveness = 90,
            MinimumBrightness = 40,
            MinimumSharpness = 40,
        };
        var provider = new RekognitionSelfieProvider(client, options, TimeProvider.System);
        var session = await provider.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var result = await provider.ReadAsync(session.Reference, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        Assert.Equal(0, client.StartRequest!.Settings.AuditImagesLimit);
        Assert.Null(client.StartRequest.Settings.OutputConfig);
        if (expected != SelfieState.Inconclusive)
        {
            Assert.Equal(["DEFAULT"], client.FaceRequest!.Attributes);
        }
    }

    [Theory]
    [InlineData(44.99f, SelfieState.Passed)]
    [InlineData(45f, SelfieState.RecaptureRequired)]
    public async Task ReadAppliesTheCalibratedPoseLimitToTheReferenceFrame(float yaw, SelfieState expected)
    {
        using var client = new Client(99, 80, yaw);
        var provider = new RekognitionSelfieProvider(client, new SelfieProviderOptions(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        var session = await provider.StartAsync(Guid.NewGuid(), ct);

        var result = await provider.ReadAsync(session.Reference, ct);

        Assert.Equal(expected, result.State);
        Assert.Equal("aws-docs-2026-10-28", result.PolicyVersion);
    }

    /// <summary>
    /// Issue #300: sessão expirada (abandono) ou que falhou no fornecedor não é reprovação da captura e não conta no
    /// limite de tentativas; vira <c>Expired</c>, com o mesmo próximo passo de nova captura.
    /// </summary>
    [Theory]
    [InlineData("EXPIRED")]
    [InlineData("FAILED")]
    public async Task ASessionThatNeverProducedAJudgementEndsExpiredNotRejected(string status)
    {
        using var client = new Client(99, 80) { Status = new LivenessSessionStatus(status) };
        var provider = new RekognitionSelfieProvider(client, new SelfieProviderOptions(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        var session = await provider.StartAsync(Guid.NewGuid(), ct);

        var result = await provider.ReadAsync(session.Reference, ct);

        Assert.Equal(new SelfieResult(SelfieState.Expired, null, "aws-docs-2026-10-28"), result);
        Assert.Null(client.FaceRequest);
    }

    [Fact]
    public async Task StartRetriesKeepTheAttemptTokenAndNewAttemptsUseAnotherToken()
    {
        using var client = new Client(99, 80);
        var provider = new RekognitionSelfieProvider(client, new SelfieProviderOptions(), TimeProvider.System);
        var attempt = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await provider.StartAsync(attempt, ct);
        Assert.Equal(attempt.ToString("N"), client.StartRequest!.ClientRequestToken);
        await provider.StartAsync(attempt, ct);
        Assert.Equal(attempt.ToString("N"), client.StartRequest!.ClientRequestToken);
        await provider.StartAsync(other, ct);
        Assert.Equal(other.ToString("N"), client.StartRequest!.ClientRequestToken);
    }

    private sealed class Client(float liveness, float brightness, float yaw = 0)
        : AmazonRekognitionClient(new BasicAWSCredentials("synthetic", "synthetic"), RegionEndpoint.USEast1)
    {
        public CreateFaceLivenessSessionRequest? StartRequest { get; private set; }
        public DetectFacesRequest? FaceRequest { get; private set; }
        public LivenessSessionStatus Status { get; init; } = LivenessSessionStatus.SUCCEEDED;

        public override Task<CreateFaceLivenessSessionResponse> CreateFaceLivenessSessionAsync(CreateFaceLivenessSessionRequest request, CancellationToken cancellationToken = default)
        {
            StartRequest = request;
            return Task.FromResult(new CreateFaceLivenessSessionResponse { SessionId = Guid.NewGuid().ToString() });
        }

        public override Task<GetFaceLivenessSessionResultsResponse> GetFaceLivenessSessionResultsAsync(GetFaceLivenessSessionResultsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetFaceLivenessSessionResultsResponse
            {
                Status = Status,
                Confidence = liveness,
                ReferenceImage = new AuditImage { Bytes = new MemoryStream([1, 2, 3]) },
            });

        public override Task<DetectFacesResponse> DetectFacesAsync(DetectFacesRequest request, CancellationToken cancellationToken = default)
        {
            FaceRequest = request;
            return Task.FromResult(new DetectFacesResponse
            {
                FaceDetails = [new FaceDetail
            {
                Confidence = 99, Quality = new ImageQuality { Brightness = brightness, Sharpness = 80 },
                Pose = new Pose { Pitch = 0, Roll = 0, Yaw = yaw },
            }]
            });
        }
    }
}
