using Duora.Infrastructure;

namespace Duora.Web;

/// <summary>Composition root do Web, separado do Program para os testes subirem a mesma app.</summary>
internal static class DuoraWebApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        BuildTimeOpenApi.UsePlaceholderConfigurationWhenGenerating(builder.Configuration);

        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHealthChecks().AddDuoraDatabase(HealthEndpoints.ReadyTag);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();

        return app;
    }
}
