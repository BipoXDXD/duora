using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Media;

public static class PhotoLibraryServiceCollectionExtensions
{
    /// <summary>
    /// Exclusão pelo titular (API-M03) e leitura de derivado aprovado (API-M04), só na Web. Chame depois de
    /// <see cref="MediaServiceCollectionExtensions.AddMediaUploads"/>: com o upload ligado, <c>Media:Deletion</c> é
    /// obrigatório e o boot falha sem ele; com o upload desligado, nada é exigido e as rotas respondem 503.
    /// </summary>
    public static IServiceCollection AddPhotoLibrary(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PhotoDeletionOptions>()
            .Bind(configuration.GetSection(PhotoDeletionOptions.Section))
            .Validate<IOptions<MediaOptions>>(
                (o, media) => !media.Value.UploadsEnabled || o.IsComplete(),
                "Media:Deletion é obrigatório com o upload ligado: EnvironmentId não vazio, OperationKeyId com 1 a 64 "
                + "caracteres [A-Za-z0-9_-] e OperationKey em base64 com 32 a 64 bytes, vindo de segredo.")
            .ValidateOnStart();

        services.AddScoped(provider => new PhotoDeletions(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<PhotoDeletionOptions>>(),
            provider.GetRequiredService<ILogger<PhotoDeletions>>()));
        services.AddScoped(provider => new OwnerMediaReads(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<OwnerMediaReads>>()));
        return services;
    }
}
