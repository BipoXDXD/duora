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
            MaximumPoseDegrees = 20,
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

    private sealed class Client(float liveness, float brightness)
        : AmazonRekognitionClient(new BasicAWSCredentials("synthetic", "synthetic"), RegionEndpoint.USEast1)
    {
        public CreateFaceLivenessSessionRequest? StartRequest { get; private set; }
        public DetectFacesRequest? FaceRequest { get; private set; }

        public override Task<CreateFaceLivenessSessionResponse> CreateFaceLivenessSessionAsync(CreateFaceLivenessSessionRequest request, CancellationToken cancellationToken = default)
        {
            StartRequest = request;
            return Task.FromResult(new CreateFaceLivenessSessionResponse { SessionId = Guid.NewGuid().ToString() });
        }

        public override Task<GetFaceLivenessSessionResultsResponse> GetFaceLivenessSessionResultsAsync(GetFaceLivenessSessionResultsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetFaceLivenessSessionResultsResponse
            {
                Status = LivenessSessionStatus.SUCCEEDED,
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
                Pose = new Pose { Pitch = 0, Roll = 0, Yaw = 0 },
            }]
            });
        }
    }
}
