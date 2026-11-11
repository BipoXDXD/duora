namespace Duora.Web.Http;

/// <summary>
/// Como uma falha vira resposta da API: Problem Details com <c>code</c>, os handlers de exceção e o bloqueio de cache.
/// Separado do composition root para os testes montarem o mesmo pipeline em volta de rotas que falham de propósito.
/// </summary>
internal static class ApiErrorHandling
{
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.AddDefaultCode);
        services.AddSingleton<AccountBusyMetrics>();
        services.AddExceptionHandler<AccountBusyExceptionHandler>();
        services.AddExceptionHandler<DatabaseUnavailableExceptionHandler>();
        return services;
    }

    /// <summary>Antes do roteamento: o handler de exceção precisa envolver tudo o que pode falhar depois.</summary>
    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(PreventCachingOfApiResponses);
        return app;
    }

    /// <summary>
    /// Respostas da API carregam dado privado ou token: nada de cache em navegador ou proxy (spec 8.1).
    /// </summary>
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
