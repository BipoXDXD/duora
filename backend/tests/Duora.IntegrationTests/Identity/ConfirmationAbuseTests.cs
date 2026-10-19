using System.Data.Common;
using System.Net;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Abusos do link de confirmação (TM-66): reaproveitar o link como oráculo de senha, furar o teto de tentativas em
/// paralelo e disputar a confirmação com um recadastro.
/// </summary>
[Trait("Threat", "TM-66")]
public sealed class ConfirmationAbuseTests(PostgresFixture postgres)
{
    private const string OwnerPassword = "senha escolhida pela titular";

    [Fact]
    public async Task ReusedLinkAfterConfirmationIsNeverAPasswordOracle()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        using var confirmation = await ConfirmAsync(browser, link, OwnerPassword);

        using var rightPassword = await ConfirmAsync(browser, link, OwnerPassword);
        using var wrongPassword = await ConfirmAsync(browser, link, "senha errada qualquer");

        Assert.Equal(HttpStatusCode.NoContent, confirmation.StatusCode);
        await ProblemAssert.IsProblemAsync(rightPassword, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        await ProblemAssert.IsProblemAsync(wrongPassword, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(rightPassword),
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongPassword));
    }

    [Fact]
    public async Task ParallelWrongPasswordsCannotSlipPastTheAttemptCeiling()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            // Isola o teto do banco: a cota por conta em memória (10 / hora) responderia 429 antes dele.
            Settings = new Dictionary<string, string?> { ["RateLimiting:TokenRedemptionPerAccount:PermitLimit"] = "1000" },
        };
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        var attackers = Enumerable.Range(0, 10).Select(_ => new Browser(factory)).ToArray();
        foreach (var attacker in attackers)
        {
            await attacker.FetchCsrfTokenAsync();
        }

        var attempts = await Task.WhenAll(attackers.Select((attacker, i) => ConfirmAsync(attacker, link, $"palpite errado {i}")));
        using var rightPasswordAfterwards = await ConfirmAsync(browser, link, OwnerPassword);

        Assert.All(attempts, attempt => Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode));
        await ProblemAssert.IsProblemAsync(rightPasswordAfterwards, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        foreach (var disposable in attempts.Cast<IDisposable>().Concat(attackers))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task ConfirmationLosingARaceWithARegistrationIsAnInvalidTokenNotAServerError()
    {
        var race = new RegistrationRace(postgres.ConnectionString);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services => services.ConfigureDbContext<DuoraDbContext>(o => o.AddInterceptors(race)),
        };
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        race.Arm(link.UserId);
        using var confirmation = await ConfirmAsync(browser, link, OwnerPassword);

        Assert.True(race.Fired, "O recadastro concorrente não chegou a acontecer.");
        await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and email_confirmed",
            link.UserId));
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Browser browser, AccountLink link, string password) =>
        browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token, password });

    /// <summary>
    /// Na hora em que a confirmação vai gravar o e-mail como confirmado, faz o que um recadastro do mesmo e-mail faz
    /// (troca security stamp e concurrency stamp) por outra conexão, já commitado. Reproduz a corrida sem sleep.
    /// </summary>
    private sealed class RegistrationRace(string connectionString) : DbCommandInterceptor
    {
        private Guid? _userId;

        public bool Fired { get; private set; }

        public void Arm(Guid userId) => _userId = userId;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await InterfereAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await InterfereAsync(command, cancellationToken);
            return result;
        }

        private async Task InterfereAsync(DbCommand command, CancellationToken cancellationToken)
        {
            var text = command.CommandText;
            if (_userId is not { } userId || Fired
                || !text.Contains("update identity.users", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("email_confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Fired = true;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var registration = new NpgsqlCommand(
                "update identity.users set security_stamp = $1, concurrency_stamp = $2 where id = $3",
                connection);
            registration.Parameters.AddWithValue(Guid.NewGuid().ToString("N"));
            registration.Parameters.AddWithValue(Guid.NewGuid().ToString("D"));
            registration.Parameters.AddWithValue(userId);
            await registration.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
