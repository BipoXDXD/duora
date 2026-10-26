using System.Security.Cryptography;
using Duora.Infrastructure.Safety;
using Duora.Web.Http;
using Npgsql;

namespace Duora.Web.Safety;

internal sealed class PhotoReviewEvidenceResult(ReviewEvidenceDelivery delivery) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        await using var owned = delivery;
        var buffer = new byte[64 * 1024];
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.ContentDisposition = "inline";
        http.Response.ContentType = "image/webp";
        // No range support, reusable URL or buffer response: each chunk requires its own live lease check.
        try
        {
            while (await owned.ReadAsync(buffer, http.RequestAborted) is var read && read != 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (Exception error) when (error is IOException or NpgsqlException or OperationCanceledException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
            {
                http.Abort();
            }
            else
            {
                http.Response.ContentType = null;
                ApiProblems.SetRetryAfter(http, TimeSpan.FromSeconds(2));
                await ApiProblems.Problem(503, ApiProblems.TemporarilyUnavailable).ExecuteAsync(http);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
