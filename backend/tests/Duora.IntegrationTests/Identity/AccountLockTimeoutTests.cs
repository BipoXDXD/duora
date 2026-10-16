using System.Net;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// A espera pelo lock da conta tem teto (<c>Identity:AccountLockTimeout</c>, <c>lock_timeout</c> do PostgreSQL):
/// passou do teto, a requisição responde 503 <c>TEMPORARILY_UNAVAILABLE</c> com <c>Retry-After</c>, nunca 500, e
/// não segura uma conexão do pool esperando.
/// </summary>
public sealed class AccountLockTimeoutTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public AccountLockTimeoutTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            Settings = new Dictionary<string, string?> { ["Identity:AccountLockTimeout"] = "00:00:00.300" },
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ALoginThatWaitsTooLongForTheAccountAnswers503()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        await using var holder = await HoldAccountAsync(account.UserId);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        await AssertTemporarilyUnavailableAsync(response);
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task AReauthenticationThatWaitsTooLongAnswers503()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await using var holder = await HoldAccountAsync(account.UserId);

        using var response = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });

        await AssertTemporarilyUnavailableAsync(response);
    }

    [Fact]
    public async Task AccountSecurityChangesThatWaitTooLongAnswer503AndChangeNothing()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await using var holder = await HoldAccountAsync(account.UserId);

        using var enrollment = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);
        using var passwordChange = await browser.PostJsonAsync(
            "/api/v1/me/password-changes",
            new { currentPassword = account.Password, newPassword = "uma frase nova e longa" });

        await AssertTemporarilyUnavailableAsync(enrollment);
        await AssertTemporarilyUnavailableAsync(passwordChange);
        await holder.DisposeAsync();
        using var me = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(0, await Scenario.ScalarAsync(
            "select count(*) from identity.user_tokens where user_id = $1",
            account.UserId));
    }

    [Fact]
    public async Task QueuedChecksOfTheSameAccountWaitWithoutHoldingDatabaseConnections()
    {
        // Sem a fila em memória, cada login esperando o lock segura uma conexão; 25 contas assim esgotam o
        // max_connections do servidor (53300) e viram 500 (visto no CI do #43).
        await using var factory = new DuoraWebFactory(_postgres.ConnectionString)
        {
            Time = _time,
            Settings = new Dictionary<string, string?>
            {
                ["Identity:AccountLockTimeout"] = "00:00:02",
                ["RateLimiting:LoginPerAccount:PermitLimit"] = "1000",
                ["RateLimiting:LoginPerAccount:Window"] = "00:15:00",
                ["RateLimiting:LoginPerIp:PermitLimit"] = "1000",
                ["RateLimiting:LoginPerIp:Window"] = "00:01:00",
            },
        };
        var account = await new IdentityScenario(_postgres, factory).CreateConfirmedAccountAsync();
        var browsers = new List<Browser>();
        for (var i = 0; i < 25; i++)
        {
            var browser = new Browser(factory);
            await browser.FetchCsrfTokenAsync();
            browsers.Add(browser);
        }

        await using var holder = await HoldAccountAsync(account.UserId);
        var logins = browsers.Select(browser => IdentityScenario.LoginAsync(browser, account.Email, account.Password)).ToList();
        var mostWaiting = 0L;
        while (!logins.All(login => login.IsCompleted))
        {
            mostWaiting = Math.Max(mostWaiting, await CountConnectionsWaitingForTheAccountLockAsync());
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(logins);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode));
        Assert.InRange(mostWaiting, 0, 1);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        browsers.ForEach(browser => browser.Dispose());
    }

    private async Task<long> CountConnectionsWaitingForTheAccountLockAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select count(*) from pg_stat_activity
            where datname = current_database() and wait_event_type = 'Lock'
              and query like '%from identity.users where id =%for no key update%'
            """,
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertTemporarilyUnavailableAsync(HttpResponseMessage response)
    {
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.InRange(ProblemAssert.RetryAfterSeconds(response), 1, 60);
    }

    /// <summary>Outra transação com a linha da conta travada, como uma verificação longa em andamento.</summary>
    private async Task<AccountHolder> HoldAccountAsync(Guid userId)
    {
        var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select 1 from identity.users where id = $1 for no key update",
            connection,
            transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new AccountHolder(connection, transaction);
    }

    private sealed class AccountHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
