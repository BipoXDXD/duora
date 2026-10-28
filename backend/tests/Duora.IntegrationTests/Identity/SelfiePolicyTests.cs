using Duora.Infrastructure.Identity;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Política calibrada de 28/10/2026 (issue #93, ADR-0029). Fronteiras on/off point de cada limiar; os valores
/// esperados são literais, não lidos da implementação.
/// </summary>
public sealed class SelfiePolicyTests
{
    private static readonly SelfieProviderOptions Calibrated = new();

    [Fact]
    public void DefaultOptionsCarryTheVersionedCalibratedPolicy()
    {
        Assert.Equal("aws-docs-2026-10-28", Calibrated.PolicyVersion);
        Assert.Equal("us-east-1", Calibrated.Region);
        Assert.Equal(80, Calibrated.MinimumLiveness);
        Assert.Equal(30, Calibrated.MinimumBrightness);
        Assert.Equal(20, Calibrated.MinimumSharpness);
        Assert.Equal(45, Calibrated.MaximumYawDegrees);
        Assert.Equal(30, Calibrated.MaximumPitchDegrees);
        Assert.Equal(45, Calibrated.MaximumRollDegrees);
        Assert.False(Calibrated.Enabled);
    }

    [Theory]
    [InlineData(80f, true)]
    [InlineData(79.99f, false)]
    [InlineData(100f, true)]
    [InlineData(0f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void LivenessPassesFromTheMinimumConfidenceInclusive(float confidence, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsLiveness(confidence));

    [Theory]
    [InlineData(30f, true)]
    [InlineData(29.99f, false)]
    [InlineData(100f, true)]
    [InlineData(float.NaN, false)]
    public void BrightnessPassesFromTheMinimumInclusive(float brightness, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsFace(Face(brightness: brightness)));

    [Theory]
    [InlineData(20f, true)]
    [InlineData(19.99f, false)]
    [InlineData(100f, true)]
    [InlineData(float.NegativeInfinity, false)]
    public void SharpnessPassesFromTheMinimumInclusive(float sharpness, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsFace(Face(sharpness: sharpness)));

    [Theory]
    [InlineData(44.99f, true)]
    [InlineData(45f, false)]
    [InlineData(-44.99f, true)]
    [InlineData(-45f, false)]
    [InlineData(0f, true)]
    [InlineData(float.NaN, false)]
    public void YawMustStayStrictlyBelowTheLimitInEitherDirection(float yaw, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsFace(Face(yaw: yaw)));

    [Theory]
    [InlineData(29.99f, true)]
    [InlineData(30f, false)]
    [InlineData(-29.99f, true)]
    [InlineData(-30f, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void PitchMustStayStrictlyBelowTheLimitInEitherDirection(float pitch, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsFace(Face(pitch: pitch)));

    [Theory]
    [InlineData(44.99f, true)]
    [InlineData(45f, false)]
    [InlineData(-44.99f, true)]
    [InlineData(-45f, false)]
    [InlineData(float.NaN, false)]
    public void RollMustStayStrictlyBelowTheLimitInEitherDirection(float roll, bool expected)
        => Assert.Equal(expected, Calibrated.AcceptsFace(Face(roll: roll)));

    [Fact]
    public void EnabledCalibratedPolicyIsValidWithPrivacyRoleAndCredentials()
        => Assert.True(Enabled().IsValid());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(180.01)]
    public void EnabledPolicyRejectsPoseLimitsOutsideTheProviderRange(double limit)
    {
        foreach (var options in new[]
        {
            Enabled(o => o.MaximumYawDegrees = limit),
            Enabled(o => o.MaximumPitchDegrees = limit),
            Enabled(o => o.MaximumRollDegrees = limit),
        })
        {
            Assert.False(options.IsValid());
        }
    }

    [Theory]
    [InlineData("eu-west-1")]
    [InlineData("")]
    public void EnabledPolicyOnlyAcceptsTheFaceLivenessRegion(string region)
        => Assert.False(Enabled(o => o.Region = region).IsValid());

    private static SelfieFaceQuality Face(float brightness = 80, float sharpness = 80, float pitch = 0, float yaw = 0, float roll = 0)
        => new(brightness, sharpness, pitch, yaw, roll);

    private static SelfieProviderOptions Enabled(Action<SelfieProviderOptions>? change = null)
    {
        var options = new SelfieProviderOptions
        {
            Enabled = true,
            PrivacyApproved = true,
            CaptureRoleArn = "arn:aws:iam::000000000000:role/synthetic",
        };
        change?.Invoke(options);
        return options;
    }
}
