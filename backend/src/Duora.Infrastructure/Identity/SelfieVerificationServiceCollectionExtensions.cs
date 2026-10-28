using Microsoft.Extensions.DependencyInjection;

namespace Duora.Infrastructure.Identity;

public static class SelfieVerificationServiceCollectionExtensions
{
    public static IServiceCollection AddSelfieVerification(this IServiceCollection services)
    {
        services.AddOptions<SelfieProviderOptions>().BindConfiguration("SelfieVerification")
            .Validate(o => o.IsValid(), "SelfieVerification requires approved privacy settings, role and calibrated thresholds.")
            .ValidateOnStart();
        services.AddOptions<SelfieAwsCredentialOptions>().BindConfiguration("SelfieVerification")
            .Validate<Microsoft.Extensions.Options.IOptions<SelfieProviderOptions>>(
                (credentials, provider) => !provider.Value.Enabled || credentials.IsComplete,
                "SelfieVerification:AwsAccessKeyId and SelfieVerification:AwsSecretAccessKey must come from secret files when enabled.")
            .ValidateOnStart();
        services.AddScoped(p => new SelfieVerificationService(
            p.GetRequiredService<Duora.Infrastructure.Persistence.DuoraDbContext>(),
            p.GetRequiredService<Microsoft.Extensions.Options.IOptions<SelfieProviderOptions>>(),
            p.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Duora.Core.Identity.ISelfieProvider>(p =>
        {
            var options = p.GetRequiredService<Microsoft.Extensions.Options.IOptions<SelfieProviderOptions>>().Value;
            return options.Enabled
                ? new RekognitionSelfieProvider(new Amazon.Rekognition.AmazonRekognitionClient(
                    p.GetRequiredService<Microsoft.Extensions.Options.IOptions<SelfieAwsCredentialOptions>>().Value.ToAwsCredentials(),
                    Amazon.RegionEndpoint.GetBySystemName(options.Region)), options, p.GetRequiredService<TimeProvider>())
                : new DisabledSelfieProvider();
        });
        services.AddScoped(p => new SelfieVerificationDispatcher(
            p.GetRequiredService<Duora.Infrastructure.Persistence.DuoraDbContext>(),
            new Duora.Infrastructure.Outbox.OutboxDeliveryQueue(p.GetRequiredService<Npgsql.NpgsqlDataSource>()),
            p.GetRequiredService<Duora.Core.Identity.ISelfieProvider>(),
            p.GetRequiredService<Microsoft.Extensions.Options.IOptions<SelfieProviderOptions>>(),
            p.GetRequiredService<TimeProvider>()));
        services.AddScoped<SelfieCaptureCredentialService>();
        return services;
    }
}

internal sealed class DisabledSelfieProvider : Duora.Core.Identity.ISelfieProvider
{
    public Task<Duora.Core.Identity.SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Selfie provider disabled.");
    public Task<Duora.Core.Identity.SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Selfie provider disabled.");
}
