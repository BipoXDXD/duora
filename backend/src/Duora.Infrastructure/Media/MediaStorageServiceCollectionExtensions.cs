using Duora.Core.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

public static class MediaStorageServiceCollectionExtensions
{
    private const string ProductionEnvironment = "Production";

    /// <summary>
    /// Registra o <see cref="IObjectStore"/> escolhido em <c>Media:Storage:Provider</c>. O host não sobe sem provedor,
    /// com o filesystem em <c>Production</c> (spec 15.4) nem com o S3 incompleto ou fora de https.
    /// </summary>
    public static IServiceCollection AddMediaStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        var isProduction = string.Equals(environmentName, ProductionEnvironment, StringComparison.OrdinalIgnoreCase);
        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.Section))
            .Validate(
                o => o.Provider != MediaStorageProvider.Unset,
                "Media:Storage:Provider é obrigatório (FileSystem em desenvolvimento, S3 em produção).")
            .Validate(
                o => !(isProduction && o.Provider == MediaStorageProvider.FileSystem),
                "Media:Storage:Provider=FileSystem não é aceito em Production: o disco do container é efêmero.")
            .Validate(
                o => o.Provider != MediaStorageProvider.FileSystem || !string.IsNullOrWhiteSpace(o.FileSystemRoot),
                "Media:Storage:FileSystemRoot é obrigatório com o provedor FileSystem.")
            .Validate(
                o => o.Provider != MediaStorageProvider.S3 || (o.S3.IsComplete() && !(isProduction && o.S3.AllowInsecureHttp)),
                "Media:Storage:S3 incompleto: Region, Bucket, AccessKeyId, SecretAccessKey e ServiceUrl https.")
            .ValidateOnStart();

        services.AddSingleton<IObjectStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
            return options.Provider switch
            {
                MediaStorageProvider.FileSystem => new FileSystemObjectStore(options.FileSystemRoot),
                MediaStorageProvider.S3 => new S3ObjectStore(options.S3),
                _ => throw new OptionsValidationException(
                    MediaStorageOptions.Section,
                    typeof(MediaStorageOptions),
                    ["Media:Storage:Provider inválido."]),
            };
        });

        return services;
    }
}
