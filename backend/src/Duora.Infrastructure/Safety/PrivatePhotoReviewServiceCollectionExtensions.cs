using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Duora.Infrastructure.Safety;

/// <summary>Internal composition only: no route, real grant, hosted producer or review-policy admission.</summary>
internal static class PrivatePhotoReviewServiceCollectionExtensions
{
    internal static IServiceCollection AddPrivatePhotoReview(this IServiceCollection services)
    {
        services.TryAddSingleton<IDbContextFactory<DuoraDbContext>>(provider =>
            new PrivatePhotoReviewDbContextFactory(provider.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddScoped(provider => new PhotoReviewAccess(
            provider.GetRequiredService<DuoraDbContext>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewAssignments(
            provider.GetRequiredService<DuoraDbContext>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewEvidence(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewRejections(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewApprovals(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewExpirations(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewCandidateAvailability(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new ApprovedMediaBackupCatalog(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new PhotoReviewService(
            provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>(), provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}

/// <summary>Each operation owns its context; all of them borrow the process's existing bounded pool.</summary>
internal sealed class PrivatePhotoReviewDbContextFactory : IDbContextFactory<DuoraDbContext>
{
    private readonly DbContextOptions<DuoraDbContext> _options;

    internal PrivatePhotoReviewDbContextFactory(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(source);
        _options = options.Options;
    }

    public DuoraDbContext CreateDbContext() => new(_options);

    public Task<DuoraDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
