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
    public void WithUploadsOnAndNoProviderTheHostRefusesToStart()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Development", new()
        {
            ["Media:Review"] = "TechnicalOnly",
        }));

        Assert.Contains("Media:Storage:Provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithUploadsOffNoStorageIsRequired()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddLogging().AddMediaUploads(configuration, "Production").BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<MediaOptions>>().Value.UploadsEnabled);
        _ = provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
    }

    [Fact]
    public void TechnicalOnlyReviewIsRefusedInProduction()
    {
        var settings = CompleteS3();
        settings["Media:Review"] = "TechnicalOnly";

        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Production", settings));

        Assert.Contains("Media:Review", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileSystemStoreServesDevelopment()
    {
        var store = Resolve("Development", new()
        {
            ["Media:Review"] = "TechnicalOnly",
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _root.FullName,
        });

        Assert.IsType<FileSystemObjectStore>(store);
    }

    [Fact]
    public void TheFileSystemStoreServesProductionFromAMountedDirectory()
    {
        // Adendo do ADR-0010 (2026-10-16): fotos no disco da VPS, num diretório do host montado no container.
        var store = Resolve("Production", new()
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _root.FullName,
        });

        Assert.IsType<FileSystemObjectStore>(store);
    }

    [Fact]
    public void TheFileSystemStoreRefusesAMissingDirectoryInProduction()
    {
        // Sem o volume montado, gravar criaria o diretório na camada efêmera do container (spec 15.4).
        var missing = Path.Combine(_root.FullName, "volume-nao-montado");

        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Production", new()
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = missing,
        }));

        Assert.Contains("FileSystemRoot", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void TheFileSystemStoreRefusesARelativeDirectoryInProduction()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve("Production", new()
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = "media",
        }));

        Assert.Contains("FileSystemRoot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideProductionAMissingDirectoryIsCreatedOnFirstWrite()
    {
        var store = Resolve("Development", new()
        {
            ["Media:Review"] = "TechnicalOnly",
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = Path.Combine(_root.FullName, "ainda-nao-existe"),
        });

        Assert.IsType<FileSystemObjectStore>(store);
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

    [Fact]
    public void TheS3OptionsNeverPrintTheirSecret()
    {
        var options = new S3StorageOptions
        {
            Region = "us-east-1",
            Bucket = "duora-media",
            AccessKeyId = "synthetic-access",
            SecretAccessKey = "CANARY-" + Guid.NewGuid(),
        };

        var printed = options.ToString();

        Assert.DoesNotContain(options.SecretAccessKey, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-access", printed, StringComparison.Ordinal);
        Assert.Contains("duora-media", printed, StringComparison.Ordinal);
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
        var services = new ServiceCollection().AddLogging().AddMediaUploads(configuration, environment);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<MediaOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
        return provider.GetRequiredService<IObjectStore>();
    }
}
