using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Duora.Web;

internal static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: só prova que o processo responde. Sem dependência, para o orquestrador não reiniciar
        // a aplicação por causa de uma queda do banco.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

        // Readiness inclui o banco: sem ele a instância não atende nenhuma operação útil, então é melhor
        // tirá-la do tráfego do que responder 500. Dependências de terceiros (e-mail, storage) ficam de fora.
        endpoints.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
    }
}
