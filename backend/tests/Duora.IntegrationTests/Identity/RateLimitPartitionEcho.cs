using System.Threading.RateLimiting;
using Duora.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Só de teste: devolve num header da resposta a chave de partição por IP, <see cref="ClientNetwork.RateLimitKey"/>,
/// calculada dentro do middleware de rate limit, sobre o mesmo <c>RemoteIpAddress</c> que as políticas por IP leem
/// ali. Assim o teste vê o corte em /64 e o IPv4 mapeado como o limitador vê, e não o endereço no fim do pipeline.
/// </summary>
/// <remarks>
/// A chave é capturada por um <c>GlobalLimiter</c> que nunca limita: o middleware o consulta em todo pedido, antes da
/// política do endpoint, então a chave chega à resposta também quando a política rejeita. Sem o header, o pedido não
/// passou pelo limitador.
/// </remarks>
internal sealed class RateLimitPartitionEcho : IStartupFilter
{
    public const string Header = "X-Test-Rate-Limit-Partition";

    private static readonly object CapturedKey = new();

    /// <summary>Registra o filtro e o limitador global que captura a chave numa fábrica de teste.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddTransient<IStartupFilter, RateLimitPartitionEcho>();
        services.PostConfigure<RateLimiterOptions>(options =>
        {
            if (options.GlobalLimiter is not null)
            {
                throw new InvalidOperationException("A aplicação passou a ter GlobalLimiter; o eco de teste o substituiria.");
            }

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = ClientNetwork.RateLimitKey(context.Connection.RemoteIpAddress);
                context.Items[CapturedKey] = key;
                return RateLimitPartition.GetNoLimiter(key);
            });
        });
    }

    /// <summary>A chave de partição que a resposta informa; falha se o header não veio.</summary>
    public static string Of(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues(Header));

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.Items.TryGetValue(CapturedKey, out var key) && key is string partition)
                {
                    context.Response.Headers[Header] = partition;
                }

                return Task.CompletedTask;
            });
            return nextMiddleware(context);
        });
        next(app);
    };
}
