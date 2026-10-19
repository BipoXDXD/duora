using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.AgeVerification;

public sealed class AgeVerificationProviderSettingsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task LegacyConfigurationCannotRegisterAnAgeProvider()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Settings = AgeVerificationScenario.FakeProviderSettings() };
        Assert.Null(factory.Services.GetService<IAgeEligibilityProvider>());
        Assert.False(factory.Services.GetRequiredService<IOptions<SelfieProviderOptions>>().Value.Enabled);
    }

    [Theory]
    [InlineData("SelfieVerification:MinimumLiveness", "0")]
    [InlineData("SelfieVerification:MinimumSharpness", "0")]
    [InlineData("SelfieVerification:PrivacyApproved", "false")]
    public void EnabledSelfieRequiresExplicitPrivacyAndCalibratedThresholds(string key, string value)
    {
        var options = new SelfieProviderOptions { Enabled = true, PrivacyApproved = true, PolicyVersion = "test", MinimumLiveness = 90, MinimumBrightness = 40, MinimumSharpness = 40, MaximumPoseDegrees = 20, CaptureRoleArn = "arn:aws:iam::000000000000:role/test" };
        if (key.EndsWith("MinimumLiveness", StringComparison.Ordinal))
        { options.MinimumLiveness = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
        if (key.EndsWith("MinimumSharpness", StringComparison.Ordinal))
        { options.MinimumSharpness = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
        if (key.EndsWith("PrivacyApproved", StringComparison.Ordinal))
        { options.PrivacyApproved = bool.Parse(value); }
        Assert.False(options.IsValid());
    }
}
