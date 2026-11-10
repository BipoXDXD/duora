using System.Diagnostics;
using System.Globalization;
using System.Net;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #455: o que login e cadastro respondem enquanto outra transação segura a linha da conta com o mesmo
/// <c>FOR NO KEY UPDATE</c> que <c>AccountSecurityService.LockAndReloadAsync</c> toma. O login desiste no
/// <c>lock_timeout</c> (55P03) e responde 503 <c>TEMPORARILY_UNAVAILABLE</c>; o cadastro do mesmo e-mail não tem esse
/// teto e espera a trava sair.
/// </summary>
public sealed class HeldAccountLockTests : IAsyncDisposable
{
    private static readonly TimeSpan AccountLockTimeout = TimeSpan.FromMilliseconds(300);

    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _factory;

    public HeldAccountLockTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = TestClock.StartingNow(),
            Settings = new Dictionary<string, string?>
            {
                ["Identity:AccountLockTimeout"] = AccountLockTimeout.ToString("c", CultureInfo.InvariantCulture),
            },
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ALoginOnAHeldAccountAnswers503WithRetryAfterAndNoInternals()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        await using var holder = await HoldAccountAsync(account.UserId);

        var waited = Stopwatch.StartNew();
        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);
        waited.Stop();

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(1, ProblemAssert.RetryAfterSeconds(response));
        await AssertNoInternalsAsync(response, account.Password);
        Assert.Null(browser.Cookie("__Host-duora"));
        Assert.InRange(waited.Elapsed, AccountLockTimeout, TimeSpan.FromSeconds(10));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"#455 login com a conta travada: {(int)response.StatusCode} em {waited.ElapsedMilliseconds} ms; " +
            $"corpo={await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    [Fact]
    public async Task ASignupOfAHeldEmailWaitsForTheLockInsteadOfAnswering503()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var signupBrowser = new Browser(_factory);
        await signupBrowser.FetchCsrfTokenAsync();
        using var loginBrowser = new Browser(_factory);
        await loginBrowser.FetchCsrfTokenAsync();
        Task<HttpResponseMessage> signup;
        var waited = Stopwatch.StartNew();
        await using (var holder = await HoldAccountAsync(account.UserId))
        {
            signup = signupBrowser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(account.Email));
            await WaitUntilAsync(async () => await CountSignupsWaitingOnTheAccountAsync() == 1 || signup.IsCompleted);
            Assert.False(signup.IsCompleted, "O cadastro deveria estar parado na trava da conta.");

            // O login da mesma conta, nessa janela, desiste no teto da conta; o cadastro continua esperando além dele.
            using var login = await IdentityScenario.LoginAsync(loginBrowser, account.Email, account.Password);
            await ProblemAssert.IsProblemAsync(login, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.False(signup.IsCompleted, "O cadastro não tem o lock_timeout da conta e ainda deveria esperar.");
        }

        using var response = await signup;
        waited.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"#455 cadastro do e-mail travado: {(int)response.StatusCode} depois de {waited.ElapsedMilliseconds} ms");
    }

    private static async Task AssertNoInternalsAsync(HttpResponseMessage response, string password)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string[] internals =
            ["Exception", "at ", "Npgsql", "55P03", "lock timeout", "select", "SELECT", "Duora.", "Microsoft.", password];
        Assert.DoesNotContain(internals, body.Contains);
    }

    private async Task<long> CountSignupsWaitingOnTheAccountAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select count(*) from pg_stat_activity
            where datname = current_database() and wait_event_type = 'Lock'
              and query like '%insert into identity.users%on conflict (normalized_email)%'
            """,
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    /// <summary>Outra transação com a linha da conta travada pela mesma consulta de <c>LockAndReloadAsync</c>.</summary>
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
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
