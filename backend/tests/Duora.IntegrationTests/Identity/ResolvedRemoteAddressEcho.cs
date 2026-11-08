using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Só de teste: devolve num header da resposta o remetente que o pipeline usou depois dos Forwarded Headers, o mesmo
/// <c>RemoteIpAddress</c> de onde o rate limit tira a partição. Lido quando a resposta começa, depois de todo o pipeline.
/// </summary>
internal sealed class ResolvedRemoteAddressEcho : IStartupFilter
{
    public const string Header = "X-Test-Resolved-Remote-Address";
    public const string NoAddress = "none";

    /// <summary>Registra o filtro numa fábrica de teste.</summary>
    public static void Register(IServiceCollection services) =>
        services.AddTransient<IStartupFilter, ResolvedRemoteAddressEcho>();

    /// <summary>O remetente resolvido que a resposta informa; falha se o header não veio.</summary>
    public static string Of(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues(Header));

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = context.Connection.RemoteIpAddress?.ToString() ?? NoAddress;
                return Task.CompletedTask;
            });
            return nextMiddleware(context);
        });
        next(app);
    };
}
