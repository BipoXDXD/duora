using Duora.Core.Media;
using Duora.Infrastructure;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Geolocation;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Participation;
using Duora.Worker.Media;
using Microsoft.Extensions.Options;

namespace Duora.Worker;

/// <summary>Composition root do worker, separado do Program para os testes subirem o mesmo host.</summary>
internal static class WorkerHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddSecretFiles();

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        }));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddInfrastructure(builder.Configuration, ConnectionShare.Worker);
        var countryDatabase = builder.Services.AddCountryDatabaseRuntime(builder.Configuration, worker: true);
        if (countryDatabase.UpdaterEnabled)
        {
            builder.Services.AddHostedService(p => new CountryDatabaseUpdateWorker(p.GetRequiredService<CountryDatabaseUpdateStore>(),
                p.GetRequiredService<CountryDatabaseUpdateService>(), p.GetRequiredService<TimeProvider>(),
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.LeaseDuration, enabled: true,
                p.GetRequiredService<ILogger<CountryDatabaseUpdateWorker>>()));
        }
        if (countryDatabase.MonitorEnabled)
        { builder.Services.AddHostedService<CountryDatabaseHealthWorker>(); }
        if (countryDatabase.RetentionEnabled)
        { builder.Services.AddHostedService<CountryDatabaseRetentionWorker>(); }
        builder.Services.AddTransactionalEmailDispatch();
        builder.Services.AddSelfieVerification();
        builder.Services.AddMediaUploads(builder.Configuration, builder.Environment.EnvironmentName);
        builder.Services.AddMediaProcessing();
        AddPhotoSanitizer(builder.Services);
        builder.Services.AddParticipation();
        builder.Services.AddHostedService<ParticipationJobService>();
        builder.Services.AddHostedService<RetentionJobService>();
        builder.Services.AddHostedService<HeartbeatService>();
        builder.Services.AddHostedService<TransactionalEmailService>();
        builder.Services.AddHostedService<SelfieVerificationProviderService>();
        builder.Services.AddHostedService<SelfieAttemptsSuspensionService>();
        builder.Services.AddHostedService<MediaProcessingService>();
        builder.Services.AddHostedService<OriginalCleanupService>();
        builder.Services.AddHostedService<PhotoDeletionCleanupService>();
        builder.Services.AddHostedService<ReviewCandidateCleanupService>();
        builder.Services.AddHostedService<ReviewPromotionCleanupService>();
        builder.Services.AddHostedService<PhotoDeletionRecordPurgeService>();

        return builder;
    }

    /// <summary>
    /// ADR-0036: transporte sem decoder. O lease cobre transferência, execução, término e a margem de storage/commit.
    /// </summary>
    private static void AddPhotoSanitizer(IServiceCollection services)
    {
        services.AddOptions<IsolatedPhotoSanitizerOptions>()
            .BindConfiguration("Media:Processing:Sanitizer")
            .Validate(o => o.IsConsistent(), "Parâmetros de Media:Processing:Sanitizer incoerentes.")
            .Validate<IServiceProvider>(
                (o, provider) => provider.MediaProcessingLease() > o.TotalTimeout + o.StorageCommitMargin,
                "Media:Processing:LeaseDuration deve cobrir o envelope completo e a margem de storage/commit.")
            .Validate<IServiceProvider>(
                (o, provider) => !provider.MediaProcessingEnabled() || o.IsConfigured(),
                "Media:Processing:Sanitizer Launcher requer socket, UID e digest do perfil compatível.")
            .ValidateOnStart();
        services.AddSingleton(provider => new IsolatedPhotoSanitizer(
            provider.GetRequiredService<IOptions<IsolatedPhotoSanitizerOptions>>().Value,
            provider.GetRequiredService<ILogger<IsolatedPhotoSanitizer>>()));
        services.AddSingleton<IPhotoSanitizer>(provider => provider.GetRequiredService<IsolatedPhotoSanitizer>());
        services.AddHostedService<MediaSanitizerReadiness>();
    }
}
