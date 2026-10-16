using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Media;

public sealed class MediaStorageConfigurationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-media-config-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void WithoutAProviderTheHostRefusesToStart()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Development", new()));

        Assert.Contains("Media:Storage:Provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileSystemStoreServesDevelopment()
    {
        var store = Resolve("Development", new()
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _root.FullName,
        });

        Assert.IsType<FileSystemObjectStore>(store);
    }

    [Fact]
    public void TheFileSystemStoreIsRefusedInProduction()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Production", new()
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _root.FullName,
        }));

        Assert.Contains("Production", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheS3StoreNeedsItsSecret()
    {
        var settings = CompleteS3();
        settings.Remove("Media:Storage:S3:SecretAccessKey");

        Assert.Throws<OptionsValidationException>(() => Resolve("Production", settings));
    }

    [Fact]
    public void TheS3StoreRefusesPlainHttpInProduction()
    {
        var settings = CompleteS3();
        settings["Media:Storage:S3:ServiceUrl"] = "http://storage.example.com";
        settings["Media:Storage:S3:AllowInsecureHttp"] = "true";

        Assert.Throws<OptionsValidationException>(() => Resolve("Production", settings));
    }

    [Fact]
    public void ACompleteS3ConfigurationServesProduction()
    {
        Assert.IsType<S3ObjectStore>(Resolve("Production", CompleteS3()));
    }

    private static Dictionary<string, string?> CompleteS3() => new()
    {
        ["Media:Storage:Provider"] = "S3",
        ["Media:Storage:S3:ServiceUrl"] = "https://storage.example.com",
        ["Media:Storage:S3:Region"] = "us-east-1",
        ["Media:Storage:S3:Bucket"] = "duora-media",
        ["Media:Storage:S3:AccessKeyId"] = "synthetic-access",
        ["Media:Storage:S3:SecretAccessKey"] = "synthetic-secret",
    };

    private static IObjectStore Resolve(string environment, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddMediaStorage(configuration, environment);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
        return provider.GetRequiredService<IObjectStore>();
    }
}
