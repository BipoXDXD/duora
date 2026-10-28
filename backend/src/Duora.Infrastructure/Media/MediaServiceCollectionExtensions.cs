using Duora.Core.Media;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Media;

internal enum MediaStorageProvider
{
    /// <summary>Sem valor configurado: o boot falha em vez de escolher um adaptador por padrão.</summary>
    Unset,
    FileSystem,
    S3,
}

/// <summary>Seção <c>Media:Storage</c>. Os segredos do S3 vêm por arquivo (Compose secrets), um por processo.</summary>
internal sealed class MediaStorageOptions
{
    public const string Section = "Media:Storage";

    public MediaStorageProvider Provider { get; set; }

    public string FileSystemRoot { get; set; } = "";

    public S3StorageOptions S3 { get; set; } = new();
}

public static class MediaServiceCollectionExtensions
{
    private const string ProductionEnvironment = "Production";

    /// <summary>
    /// Registra o upload de fotos (ADR-0025): opções da seção <c>Media</c>, o <see cref="IObjectStore"/> de
    /// <c>Media:Storage:Provider</c> e o serviço de recepção. Sem <c>Media:Review</c>, o upload fica desligado; o
    /// storage só deixa de ser exigido sem <c>Media:Storage:Provider</c> (adendo de 2026-10-19 do ADR-0025): com o
    /// filesystem em <c>Production</c>, as áreas montadas e marcadas são exigidas mesmo com o upload desligado. O host
    /// não sobe com <c>TechnicalOnly</c> em <c>Production</c>, nem com o filesystem em <c>Production</c> fora de um
    /// diretório absoluto que já exista (o volume do host montado; spec 15.4 e adendo do ADR-0010), nem com o upload
    /// ligado e o storage ausente, incompleto ou fora de https.
    /// </summary>
    public static IServiceCollection AddMediaUploads(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        var isProduction = string.Equals(environmentName, ProductionEnvironment, StringComparison.OrdinalIgnoreCase);
        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.Section))
            .Validate(
                o => o.Review is not { } review || MediaReview.IsPermitted(review, environmentName),
                $"Media:Review não é aceito em {environmentName}: TechnicalOnly só vale em Development/Testing e HumanReview também em Production (ADR-0025/0035).")
            .Validate(
                o => o.MaxPendingUploadsPerAccount >= 1
                    && o.MaxQueuedUploads >= 1
                    && o.UploadTimeout > TimeSpan.Zero
                    && o.MinUploadBytesPerSecond > 0
                    && o.MinUploadRateGracePeriod > TimeSpan.FromSeconds(1),
                "Media:MaxPendingUploadsPerAccount e Media:MaxQueuedUploads devem ser ao menos 1; Media:UploadTimeout e a taxa mínima, positivos; "
                + "a tolerância da taxa, acima de 1 s (o Kestrel recusa menos).")
            .ValidateOnStart();

        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.Section))
            .Validate<IOptions<MediaOptions>>(
                (o, media) => !media.Value.UploadsEnabled || o.Provider != MediaStorageProvider.Unset,
                "Media:Storage:Provider é obrigatório com o upload ligado (FileSystem ou S3).")
            .Validate(
                o => o.Provider != MediaStorageProvider.FileSystem || !string.IsNullOrWhiteSpace(o.FileSystemRoot),
                "Media:Storage:FileSystemRoot é obrigatório com o provedor FileSystem.")
            .Validate(
                o => !isProduction || o.Provider != MediaStorageProvider.FileSystem || IsMountedDirectory(o.FileSystemRoot),
                "Media:Storage:FileSystemRoot em Production precisa ser um caminho absoluto, com o marcador "
                + $"{FileSystemObjectStore.AreaMarkerFileName} em cada área (quarantine e derivatives) gravado pelo "
                + "Ansible no diretório do host montado. Sem ele, as fotos iriam para o disco efêmero do container.")
            .Validate<IOptions<MediaOptions>>(
                (o, media) => o.Provider != MediaStorageProvider.S3 || o.S3.RequestTimeout >= media.Value.UploadTimeout,
                "Media:Storage:S3:RequestTimeout deve ser pelo menos Media:UploadTimeout: o PUT transmite o corpo do cliente.")
            .Validate(
                o => o.Provider != MediaStorageProvider.S3 || (o.S3.IsComplete() && !(isProduction && o.S3.AllowInsecureHttp)),
                "Media:Storage:S3 incompleto: Region, Bucket, AccessKeyId, SecretAccessKey e ServiceUrl https.")
            .ValidateOnStart();

        services.AddSingleton<IObjectStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
            return options.Provider switch
            {
                MediaStorageProvider.FileSystem => new FileSystemObjectStore(options.FileSystemRoot, createMissingDirectories: !isProduction),
                MediaStorageProvider.S3 => new S3ObjectStore(options.S3, provider.GetRequiredService<ILogger<S3ObjectStore>>()),
                // Só com o upload desligado (a validação garante): o serviço de upload existe, mas não grava nada.
                _ => new UnconfiguredObjectStore(),
            };
        });
        services.AddScoped(provider => new PhotoUploads(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<AccountService>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaOptions>>(),
            provider.GetRequiredService<ILogger<PhotoUploads>>()));

        return services.AddPrivatePhotoReview();
    }

    // Em Production o adaptador não cria nada: área sem o marcador do Ansible quer dizer montagem faltando.
    private static bool IsMountedDirectory(string root) =>
        !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && FileSystemObjectStore.HasMountedAreas(root);
}
