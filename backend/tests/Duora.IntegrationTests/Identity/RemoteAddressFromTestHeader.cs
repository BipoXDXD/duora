using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// O TestServer não tem conexão de rede; este filtro, só de teste, põe o IP do remetente antes de todo o pipeline.
/// </summary>
internal sealed class RemoteAddressFromTestHeader : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    /// <summary>Registra o filtro numa fábrica de teste.</summary>
    public static void Register(IServiceCollection services) =>
        services.AddTransient<IStartupFilter, RemoteAddressFromTestHeader>();

    /// <summary>Headers de uma mutação same-origin vinda de <paramref name="address"/>.</summary>
    public static MutationHeaders From(string address) =>
        new(Extra: new Dictionary<string, string> { [Header] = address });

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}
