using System.Net;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    // O 503 sozinho não diz se foi o banco nem qual falha (#447): o handler deixa um Warning com o que identifica a
    // falha e a requisição, nunca a exceção, a mensagem do driver nem a connection string.
    [Fact]
    public async Task AnUnreachableDatabaseLeavesAWarningWithoutInternals()
    {
        var connection = new CanaryConnectionString();
        var logs = new CapturingLoggerProvider();
        await using var factory = new DuoraWebFactory(connection.Value, logs);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync("/api/v1/auth/login", LoginBody);

        var body = await ProblemAssert.IsProblemAsync(
            response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        var traceId = (string?)body["traceId"] ?? throw new InvalidOperationException("Sem traceId.");
        var warning = Assert.Single(
            logs.Entries,
            entry => entry.Category == typeof(DatabaseUnavailableExceptionHandler).FullName);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Contains(nameof(NpgsqlException), warning.Text, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/login", warning.Text, StringComparison.Ordinal);
        Assert.Contains(traceId, warning.Text, StringComparison.Ordinal);
        string[] internals = ["127.0.0.1", "Port=", "Username", "Host=", "   at ", "Failed to connect"];
        Assert.DoesNotContain(internals, leak => warning.Text.Contains(leak, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Entries, entry => entry.Text.Contains(connection.Password, StringComparison.Ordinal));
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
    public void ServerSideUnavailabilityIsRecognizedWhenWrappedByEf(string sqlState)
    {
        var error = new PostgresException("indisponível", "FATAL", "FATAL", sqlState);
        var wrapped = new InvalidOperationException("transient", error);

        Assert.True(DatabaseUnavailability.IsTemporary(wrapped));
        Assert.Same(error, DatabaseUnavailability.TemporaryCause(wrapped));
    }

    [Theory]
    [InlineData("53300")]
    [InlineData("57P01")]
    [InlineData("57P03")]
    [InlineData("08006")]
    public void ServerSideUnavailabilityIsRecognizedWhenWrappedByANpgsqlException(string sqlState)
    {
        var error = new PostgresException("indisponível", "FATAL", "FATAL", sqlState);
        // Caso defensivo, montado à mão: não foi confirmado que o Npgsql 10.0.3 embrulhe assim um erro do servidor.
        // Se acontecer, a NpgsqlException por fora não pode esconder o SqlState do servidor.
        var wrapped = new InvalidOperationException("transient", new NpgsqlException("driver", error));

        Assert.True(DatabaseUnavailability.IsTemporary(wrapped));
        Assert.Same(error, DatabaseUnavailability.TemporaryCause(wrapped));
    }

    // Caso defensivo, montado à mão: numa sonda real, o Npgsql 10.0.3 não produziu Npgsql -> Timeout -> PG 57014. Se
    // acontecer, o IsTransient vem da interna imediata, que é o timeout do cliente, não o erro do servidor, então o
    // 57014 mais fundo não pode transformar a falha em 500.
    [Fact]
    public void AClientTimeoutIsUnavailabilityEvenWithAServerErrorDeeperInTheChain()
    {
        var canceled = new PostgresException("cancelado", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled);
        var timeout = new NpgsqlException("driver", new TimeoutException("timeout", canceled));

        Assert.True(DatabaseUnavailability.IsTemporary(timeout));
        Assert.Same(timeout, DatabaseUnavailability.TemporaryCause(timeout));
    }

    [Theory]
    [InlineData("23505")]
    [InlineData("42P01")]
    public void OrdinaryDatabaseErrorsAreNotUnavailability(string sqlState)
    {
        var error = new PostgresException("erro comum", "ERROR", "ERROR", sqlState);

        Assert.False(DatabaseUnavailability.IsTemporary(error));
        Assert.False(DatabaseUnavailability.IsTemporary(new InvalidOperationException("wrapped", error)));
        Assert.False(DatabaseUnavailability.IsTemporary(new InvalidOperationException("sem banco")));
    }

    // Serialização, deadlock e lock_timeout crus são passageiros como os embrulhados (#465): o cliente pode repetir,
    // então não são erro de SQL comum nem 500. O EF por fora não esconde o SqlState.
    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("55P03")]
    public void ARetryableServerErrorIsTemporaryAlsoWhenRaw(string sqlState)
    {
        var error = new PostgresException("erro comum", "ERROR", "ERROR", sqlState);

        Assert.Same(error, DatabaseUnavailability.TemporaryCause(error));
        Assert.Same(error, DatabaseUnavailability.TemporaryCause(new InvalidOperationException("wrapped", error)));
    }

    // Para o Npgsql, constraint e objeto inexistente não são passageiros: embrulhados por ele continuam erro de SQL.
    [Theory]
    [InlineData("23505")]
    [InlineData("42P01")]
    public void AServerErrorThatNpgsqlDoesNotRetryIsNotUnavailabilityWhenWrappedByIt(string sqlState)
    {
        var error = new PostgresException("erro comum", "ERROR", "ERROR", sqlState);

        Assert.False(DatabaseUnavailability.IsTemporary(new NpgsqlException("driver", error)));
        Assert.False(DatabaseUnavailability.IsTemporary(
            new NpgsqlException("driver", new NpgsqlException("driver", error))));
    }

    // Serialização, deadlock e lock_timeout embrulhados pelo Npgsql eram 503 na main (o IsTransient dele desce até o
    // erro do servidor; sonda com o 10.0.3) e continuam: o cliente pode repetir. A causa apontada é o erro do
    // servidor, para o Warning levar o SqlState dele. Cru, o mesmo erro continua fora deste handler, como na main.
    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("55P03")]
    public void ARetryableServerErrorWrappedByNpgsqlIsUnavailabilityAsInMain(string sqlState)
    {
        var error = new PostgresException("erro comum", "ERROR", "ERROR", sqlState);
        var once = new NpgsqlException("driver", error);
        var twice = new NpgsqlException("driver", once);

        Assert.Same(error, DatabaseUnavailability.TemporaryCause(once));
        Assert.Same(error, DatabaseUnavailability.TemporaryCause(twice));
    }

    // Caso defensivo, montado à mão: a outra direção da descida, que passa só por NpgsqlException. Um timeout do
    // cliente sob dois embrulhos encerra a descida, e o 57014 mais fundo não faz a falha virar erro de SQL comum. O que
    // este teste fixa é a causa apontada por TemporaryCause, o embrulho de fora. O status não depende dela: se só o
    // embrulho de fora fosse recusado, a descida pararia no de dentro, que também é indisponibilidade.
    [Fact]
    public void AClientTimeoutWrappedTwiceByNpgsqlIsUnavailability()
    {
        var canceled = new PostgresException("cancelado", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled);
        var timeout = new TimeoutException("timeout", canceled);
        var wrapped = new NpgsqlException("driver", new NpgsqlException("driver", timeout));

        Assert.True(DatabaseUnavailability.IsTemporary(wrapped));
        Assert.Same(wrapped, DatabaseUnavailability.TemporaryCause(wrapped));
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

        public PasswordVerificationResult VerifyHashedPassword(
            DuoraUser user, string hashedPassword, string providedPassword) =>
            throw new InvalidOperationException("Falha inesperada de teste.");
    }
}
