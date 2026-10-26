using Duora.Core.Media;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Media;

public static class MediaProcessingServiceCollectionExtensions
{
    /// <summary>
    /// Registra o consumidor <c>media-processing</c> (ADR-0025). Quem chama registra antes o upload
    /// (<see cref="MediaServiceCollectionExtensions.AddMediaUploads"/>) e um <see cref="IPhotoSanitizer"/>: o decoder
    /// fica no processo que o hospeda, nunca aqui.
    /// </summary>
    public static IServiceCollection AddMediaProcessing(this IServiceCollection services)
    {
        services.AddOptions<MediaProcessingOptions>()
            .BindConfiguration(MediaProcessingOptions.Section)
            .Validate(o => o.IsConsistent(), "Parâmetros de Media:Processing incoerentes.")
            .ValidateOnStart();

        services.AddScoped(provider => new MediaProcessingDispatcher(
            new OutboxDeliveryQueue(provider.GetRequiredService<NpgsqlDataSource>()),
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<IPhotoSanitizer>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaOptions>>().Value,
            provider.GetRequiredService<IOptions<MediaProcessingOptions>>().Value,
            provider.GetRequiredService<ILogger<MediaProcessingDispatcher>>()));

        services.AddSingleton(provider => new OriginalCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<OriginalCleanupDispatcher>>()));

        services.AddSingleton(provider => new ReviewCandidateCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<ReviewCandidateCleanupDispatcher>>()));

        services.AddSingleton(provider => new ApprovedCandidateCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<ApprovedCandidateCleanupDispatcher>>()));

        services.AddSingleton(provider => new ExpiredCandidateCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<ExpiredCandidateCleanupDispatcher>>()));

        services.AddSingleton(provider => new UnavailableCandidateCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<UnavailableCandidateCleanupDispatcher>>()));

        services.AddSingleton(provider => new AppealCandidateCleanupDispatcher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<MediaStorageOptions>>().Value.Provider != MediaStorageProvider.Unset,
            provider.GetRequiredService<ILogger<AppealCandidateCleanupDispatcher>>()));

        return services;
    }

    /// <summary>Lease do consumidor, para o host conferir que ele cobre o tempo máximo da sanitização.</summary>
    public static TimeSpan MediaProcessingLease(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<MediaProcessingOptions>>().Value.LeaseDuration;

    /// <summary>Allows the host to require launcher readiness only when processing is configured.</summary>
    public static bool MediaProcessingEnabled(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<MediaOptions>>().Value.UploadsEnabled;
}
