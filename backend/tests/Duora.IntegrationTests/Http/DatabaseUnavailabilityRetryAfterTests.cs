using System.Globalization;
using Duora.Web.Http;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// O <c>Retry-After</c> de cada causa de banco sai de <see cref="DatabaseUnavailability"/> (#493): contenção e
/// timeout de comando passam sozinhos em instantes (2 s), banco fora do alcance leva mais (5 s). Os endpoints que
/// montam o 503 sem passar pelo handler global (revisão de foto, mídia, bloqueio, suspensão) chamam estes métodos em
/// vez de escolher o prazo.
/// </summary>
public sealed class DatabaseUnavailabilityRetryAfterTests
{
    [Fact]
    public void ContentionWithoutACauseAnswers2Seconds()
    {
        var http = new DefaultHttpContext();

        var result = DatabaseUnavailability.Contention(http);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("2", http.Response.Headers.RetryAfter.ToString());
    }

    [Theory]
    [InlineData("40001", 2)]
    [InlineData("40P01", 2)]
    [InlineData("55P03", 2)]
    [InlineData("57014", 2)]
    [InlineData("53300", 5)]
    [InlineData("57P01", 5)]
    [InlineData("08006", 5)]
    public void EachServerErrorAnswersTheRetryAfterOfItsCause(string sqlState, int seconds)
    {
        var http = new DefaultHttpContext();

        var result = DatabaseUnavailability.Unavailable(http, new PostgresException("x", "ERROR", "ERROR", sqlState));

        Assert.Equal(503, result.StatusCode);
        Assert.Equal(seconds.ToString(CultureInfo.InvariantCulture), http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void AClientFailureAnswers5Seconds()
    {
        var http = new DefaultHttpContext();

        DatabaseUnavailability.Unavailable(http, new NpgsqlException("x", new TimeoutException()));

        Assert.Equal("5", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void ACommandTimeoutIsNotLoggedAsContention()
    {
        Assert.False(DatabaseUnavailability.IsContention(new PostgresException("x", "ERROR", "ERROR", "57014")));
    }
}
