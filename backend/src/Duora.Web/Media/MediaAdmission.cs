using Duora.Infrastructure.Media;
using Duora.Web.Http;
using Npgsql;

namespace Duora.Web.Media;

/// <summary>
/// Gate comum de API-M03/M04: sem <c>Media:Review</c> as rotas respondem 503 <c>MEDIA_UPLOADS_DISABLED</c>, como o
/// upload (ADR-0025); registrar a rota não admite política nova. Espera de lock e cancelamento do banco viram 503 com
/// Retry-After, nunca 500 nem texto do erro.
/// </summary>
internal static class MediaAdmission
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

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
                return Unavailable(http);
            }
            catch (IOException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return Unavailable(http);
            }
        });

    private static bool IsTransient(PostgresException error) => error.SqlState is PostgresErrorCodes.LockNotAvailable
        or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.QueryCanceled;

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Unavailable(HttpContext http)
    {
        ApiProblems.SetRetryAfter(http, RetryAfter);
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
    }
}
