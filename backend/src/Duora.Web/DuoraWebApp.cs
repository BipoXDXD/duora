using System.Text.Json.Serialization;
using Duora.Infrastructure;
using Duora.Infrastructure.Profiles;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Profiles;
using Duora.Web.Safety;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace Duora.Web;

/// <summary>Composition root do Web, separado do Program para os testes subirem a mesma app.</summary>
internal static class DuoraWebApp
{
    /// <summary>Teto de corpo para JSON comum (spec 4.3); upload terá limite próprio por rota.</summary>
    private const long MaxRequestBodyBytes = 32 * 1024;

    private const int MaxJsonDepth = 16;

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(BuildTimeOpenApi.BuilderOptions(args));
        builder.Configuration.AddSecretFiles();
        BuildTimeOpenApi.UsePlaceholderConfigurationWhenGenerating(builder.Configuration);

        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.AddDefaultCode);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            // DTO estrito (spec 8.1): campo desconhecido ou repetido é 400, nunca ignorado.
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.AllowDuplicateProperties = false;
            options.SerializerOptions.MaxDepth = MaxJsonDepth;
        });
        builder.Services.AddOpenApi(options =>
        {
            PatchFieldOpenApi.Configure(options);
            ProfileEndpoints.ConfigureOpenApi(options);
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddDuoraWebIdentity(builder.Configuration);
        builder.Services.AddDuoraProfiles();
        builder.Services.AddDuoraSafety();
        builder.Services.AddHealthChecks().AddDuoraDatabase(HealthEndpoints.ReadyTag);

        // Negar por padrão: toda rota exige sessão, e as públicas declaram AllowAnonymous.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddAntiforgery(AntiforgeryTokens.Configure);
        builder.Services.AddSingleton<TrustedOrigins>();
        builder.Services.AddAuthRateLimiting();
        builder.Services.AddBlockRateLimiting();
        builder.Services.AddTrustedReverseProxy();

        var app = builder.Build();

        // Primeiro: tudo o que vem depois (rate limit por IP, esquema https) precisa do cliente real.
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(PreventCachingOfApiResponses);
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<CsrfProtectionMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
        }

        app.MapHealthEndpoints();

        var api = app.MapGroup("/api/v1");
        api.MapAuthEndpoints();
        api.MapMeEndpoints();
        api.MapProfileEndpoints();
        api.MapBlockEndpoints();

        return app;
    }

    /// <summary>Respostas da API carregam dado privado ou token: nada de cache em navegador ou proxy (spec 8.1).</summary>
    private static Task PreventCachingOfApiResponses(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Task.CompletedTask;
            });
        }

        return next();
    }
}
