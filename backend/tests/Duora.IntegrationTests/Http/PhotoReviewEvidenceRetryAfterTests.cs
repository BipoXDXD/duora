using Duora.Infrastructure.Safety;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// A entrega da evidência que falha antes do primeiro byte responde 503. A falha do banco na checagem do lease segue
/// a fonte única do <c>Retry-After</c> (<see cref="Duora.Web.Http.DatabaseUnavailability"/>): contenção 2 s, banco fora do
/// alcance 5 s. A falha de I/O, que não identifica o banco, tem o prazo próprio da entrega, 2 s (#493).
/// </summary>
public sealed class PhotoReviewEvidenceRetryAfterTests
{
    [Theory]
    [InlineData("40001", "2")]
    [InlineData("55P03", "2")]
    [InlineData("57014", "2")]
    [InlineData("53300", "5")]
    [InlineData("57P01", "5")]
    public async Task AServerErrorWhileCheckingTheLeaseFollowsTheDatabaseSource(string sqlState, string retryAfter)
    {
        var failure = new PostgresException("x", "ERROR", "ERROR", sqlState);

        var http = await DeliverAsync(failure);

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal(retryAfter, http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task AClientFailureWhileCheckingTheLeaseAnswersTheLongerDatabaseRetryAfter()
    {
        var http = await DeliverAsync(new NpgsqlException("x", new TimeoutException()));

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal("5", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task AnIoFailureAnswersTheDeliveryRetryAfter()
    {
        var http = await DeliverAsync(new IOException("x"));

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal("2", http.Response.Headers.RetryAfter.ToString());
    }

    private static async Task<DefaultHttpContext> DeliverAsync(Exception failure)
    {
        await using var lease = new PhotoReviewEvidenceLease([1, 2, 3], _ => throw failure);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();

        await new Duora.Web.Safety.PhotoReviewEvidenceResult(new ReviewEvidenceDelivery(lease)).ExecuteAsync(http);

        return http;
    }
}
