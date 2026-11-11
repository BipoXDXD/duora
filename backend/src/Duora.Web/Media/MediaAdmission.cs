using Duora.Infrastructure.Media;
using Duora.Web.Http;
using Npgsql;

namespace Duora.Web.Media;

/// <summary>
/// Gate comum de API-M03/M04: sem <c>Media:Review</c> as rotas respondem 503 <c>MEDIA_UPLOADS_DISABLED</c>, como o
/// upload (ADR-0025); registrar a rota não admite política nova. Cancelamento do banco (57014) e falha de I/O viram 503
/// com Retry-After, nunca 500 nem texto do erro; a contenção fica com o handler global.
/// </summary>
internal static class MediaAdmission
{
    // Falha de I/O do storage: não é o banco, que usa DatabaseUnavailability.
    private static readonly TimeSpan StorageRetryAfter = TimeSpan.FromSeconds(2);

    public static RouteHandlerBuilder RequireMediaAdmission(this RouteHandlerBuilder route) => route
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (!http.RequestServices.GetRequiredService<PhotoUploads>().UploadsEnabled)
            {
                return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.MediaUploadsDisabled);
            }

            try
            {
                return await next(invocation);
            }
            catch (PostgresException error) when (!http.RequestAborted.IsCancellationRequested && IsTransient(error))
            {
                return DatabaseUnavailability.Unavailable(http, error);
            }
            catch (IOException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return StorageUnavailable(http);
            }
        });

    // Contenção (40001, 40P01, 55P03) segue para o handler global, que decide o Retry-After (#465).
    private static bool IsTransient(PostgresException error) => error.SqlState is PostgresErrorCodes.QueryCanceled;

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult StorageUnavailable(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, StorageRetryAfter);
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
    }
}
