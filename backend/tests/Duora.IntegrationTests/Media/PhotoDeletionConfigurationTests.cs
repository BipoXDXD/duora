using System.Security.Cryptography;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// O material da chave de operação de API-M03 (PhotoDeletionIntentBinding) vem só de configuração/segredo: com o
/// upload ligado, a Web não sobe sem ele; com o upload desligado, nada é exigido.
/// </summary>
public sealed class PhotoDeletionConfigurationTests
{
    [Fact]
    public void WithUploadsOnAndNoDeletionKeyTheWebRefusesToStart()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve(Enabled()));

        Assert.Contains("Media:Deletion", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(65)]
    public void AKeyOutsideThirtyTwoToSixtyFourBytesIsRefused(int length)
    {
        var settings = Complete();
        settings["Media:Deletion:OperationKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        var error = Assert.Throws<OptionsValidationException>(() => Resolve(settings));

        Assert.Contains("Media:Deletion", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Media:Deletion:OperationKey", "não é base64")]
    [InlineData("Media:Deletion:OperationKeyId", "chave com espaço")]
    [InlineData("Media:Deletion:OperationKeyId", "")]
    [InlineData("Media:Deletion:EnvironmentId", "00000000-0000-0000-0000-000000000000")]
    public void AMalformedSettingIsRefused(string setting, string value)
    {
        var settings = Complete();
        settings[setting] = value;

        Assert.Throws<OptionsValidationException>(() => Resolve(settings));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void ACompleteConfigurationIsAccepted(int length)
    {
        var settings = Complete();
        settings["Media:Deletion:OperationKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        var options = Resolve(settings);

        Assert.Equal("test-v1", options.OperationKeyId);
    }

    [Fact]
    public void WithUploadsOffNoKeyIsRequired()
    {
        var options = Resolve([]);

        Assert.Equal(Guid.Empty, options.EnvironmentId);
    }

    private static Dictionary<string, string?> Enabled() => new()
    {
        ["Media:Review"] = "TechnicalOnly",
        ["Media:Storage:Provider"] = "FileSystem",
        ["Media:Storage:FileSystemRoot"] = Path.GetTempPath(),
    };

    private static Dictionary<string, string?> Complete()
    {
        var settings = Enabled();
        settings["Media:Deletion:EnvironmentId"] = Guid.NewGuid().ToString();
        settings["Media:Deletion:OperationKeyId"] = "test-v1";
        settings["Media:Deletion:OperationKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return settings;
    }

    private static PhotoDeletionOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddMediaUploads(configuration, "Development")
            .AddPhotoLibrary(configuration)
            .BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PhotoDeletionOptions>>().Value;
    }
}
