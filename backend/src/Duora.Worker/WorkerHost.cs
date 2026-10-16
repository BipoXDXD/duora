using Duora.Core.Media;
using Duora.Infrastructure;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Media;
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
        builder.Services.AddTransactionalEmailDispatch();
        builder.Services.AddAgeVerification();
        builder.Services.AddAgeVerificationDispatch();
        builder.Services.AddMediaUploads(builder.Configuration, builder.Environment.EnvironmentName);
        builder.Services.AddMediaProcessing();
        AddPhotoSanitizer(builder.Services);
        builder.Services.AddHostedService<HeartbeatService>();
        builder.Services.AddHostedService<TransactionalEmailService>();
        builder.Services.AddHostedService<AgeVerificationProviderService>();
        builder.Services.AddHostedService<MediaProcessingService>();

        return builder;
    }

    /// <summary>
    /// O decoder de imagem só existe neste processo (ADR-0025). O lease da fila tem de passar do tempo máximo da
    /// sanitização, senão outro worker retoma o upload enquanto o primeiro ainda decodifica.
    /// </summary>
    private static void AddPhotoSanitizer(IServiceCollection services)
    {
        services.AddOptions<PhotoSanitizerOptions>()
            .BindConfiguration("Media:Processing:Sanitizer")
            .Validate(o => o.IsConsistent(), "Parâmetros de Media:Processing:Sanitizer incoerentes.")
            .Validate<IServiceProvider>(
                (o, provider) => provider.MediaProcessingLease() > o.Timeout * 2,
                "Media:Processing:LeaseDuration deve passar de duas vezes o timeout da sanitização.")
            .ValidateOnStart();
        services.AddSingleton<IPhotoSanitizer>(provider =>
            new ImageSharpPhotoSanitizer(provider.GetRequiredService<IOptions<PhotoSanitizerOptions>>().Value));
    }
}
