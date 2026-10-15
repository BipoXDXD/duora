using Duora.Infrastructure;

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
        builder.Services.AddHostedService<HeartbeatService>();

        return builder;
    }
}
