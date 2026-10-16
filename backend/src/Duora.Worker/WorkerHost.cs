using Duora.Infrastructure;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Identity;

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
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddTransactionalEmailDispatch();
        builder.Services.AddAgeVerification();
        builder.Services.AddAgeVerificationDispatch();
        builder.Services.AddHostedService<HeartbeatService>();
        builder.Services.AddHostedService<TransactionalEmailService>();
        builder.Services.AddHostedService<AgeVerificationProviderService>();

        return builder;
    }
}
