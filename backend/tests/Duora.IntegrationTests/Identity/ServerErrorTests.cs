using System.Net;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Erros do servidor em Problem Details sem nada de dentro (exceção, pilha, driver, SQL, namespace, senha da
/// connection string). Banco inacessível, pool esgotado ou servidor sem conexões é indisponibilidade passageira:
/// <c>503 TEMPORARILY_UNAVAILABLE</c> com <c>Retry-After</c>. Qualquer outra falha inesperada continua
/// <c>500 INTERNAL_ERROR</c>.
/// </summary>
public sealed class ServerErrorTests(PostgresFixture postgres)
{
    private static readonly object LoginBody = new { email = "alguem@example.com", password = "qualquer senha longa" };

    [Fact]
    public async Task AnUnreachableDatabaseIsTemporarilyUnavailableWithoutInternals()
    {
        var connection = new CanaryConnectionString();
        await using var factory = new DuoraWebFactory(connection.Value);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync("/api/v1/auth/login", LoginBody);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.InRange(ProblemAssert.RetryAfterSeconds(response), 1, 60);
        await AssertNoInternalsAsync(response, connection.Password);
    }

    [Fact]
    public async Task AnExhaustedPoolFailsFastAsTemporarilyUnavailable()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Database:MaxPoolSize"] = "1",
                ["Database:PoolTimeout"] = "00:00:01",
            },
        };
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        await using var held = await factory.Services.GetRequiredService<NpgsqlDataSource>()
            .OpenConnectionAsync(TestContext.Current.CancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        using var response = await browser.PostJsonAsync("/api/v1/auth/login", LoginBody);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.True(
            System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
            "O pool esgotado deveria falhar no Timeout de 1 s, não no timeout do comando.");
    }

    [Fact]
    public async Task AnyOtherUnexpectedFailureIsAGenericServerError()
    {
        var connection = new CanaryConnectionString();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.AddScoped<IPasswordHasher<DuoraUser>, ExplodingPasswordHasher>(),
        };
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync("/api/v1/auth/login", LoginBody);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.False(response.Headers.Contains("Retry-After"));
        await AssertNoInternalsAsync(response, connection.Password);
    }

    [Theory]
    [InlineData("53300")]
    [InlineData("57P01")]
    [InlineData("57P03")]
    [InlineData("08006")]
    public void ServerSideUnavailabilityIsRecognizedEvenWhenWrapped(string sqlState)
    {
        var error = new PostgresException("indisponível", "FATAL", "FATAL", sqlState);

        Assert.True(DatabaseUnavailability.IsTemporary(new InvalidOperationException("transient", error)));
    }

    [Theory]
    [InlineData("23505")]
    [InlineData("42P01")]
    [InlineData("40001")]
    public void OrdinaryDatabaseErrorsAreNotUnavailability(string sqlState)
    {
        var error = new PostgresException("erro comum", "ERROR", "ERROR", sqlState);

        Assert.False(DatabaseUnavailability.IsTemporary(new InvalidOperationException("wrapped", error)));
        Assert.False(DatabaseUnavailability.IsTemporary(new InvalidOperationException("sem banco")));
    }

    private static async Task AssertNoInternalsAsync(HttpResponseMessage response, string password)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string[] internals = ["Exception", "at ", "Npgsql", "select", "SELECT", "Duora.", "Microsoft.", password];
        Assert.DoesNotContain(internals, body.Contains);
    }

    private sealed class ExplodingPasswordHasher : IPasswordHasher<DuoraUser>
    {
        public string HashPassword(DuoraUser user, string password) =>
            throw new InvalidOperationException("Falha inesperada de teste.");

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword) =>
            throw new InvalidOperationException("Falha inesperada de teste.");
    }
}
