using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #461: o upsert do cadastro espera a linha da conta com o mesmo teto do login (<c>Identity:AccountLockTimeout</c>).
/// Quando o teto estoura, a resposta é a mesma de um cadastro comum (TM-06): quem chama não fica sabendo que a conta
/// existe nem que outra operação a segura.
/// </summary>
public sealed class RegistrationLockTimeoutTests : IAsyncDisposable
{
    private static readonly TimeSpan AccountLockTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Folga larga para máquina carregada; sem o teto, o cadastro só termina quando a trava sai.</summary>
    private static readonly TimeSpan ResponseLimit = TimeSpan.FromSeconds(10);

    /// <summary>Headers que mudam a cada requisição e não dizem nada sobre a conta.</summary>
    private static readonly HashSet<string> PerRequestHeaders = new(StringComparer.OrdinalIgnoreCase) { "Date" };

    private const string GaveUpLogMessage = "gave up waiting for the account row lock";

    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;

    public RegistrationLockTimeoutTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs)
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
    public async Task ASignupOfAHeldAccountGivesUpAtTheCeilingWithTheSameResponseAsANewEmail()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var newEmailBrowser = new Browser(_factory);
        await newEmailBrowser.FetchCsrfTokenAsync();
        using var newEmail = await newEmailBrowser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(IdentityScenario.NewEmail()));
        using var heldBrowser = new Browser(_factory);
        await heldBrowser.FetchCsrfTokenAsync();

        var (held, waited) = await RegisterWhileHeldAsync(account, heldBrowser);

        using (held)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"#461 cadastro da conta travada: {(int)held.StatusCode} em {waited.TotalMilliseconds:F0} ms; " +
                $"headers={string.Join("; ", Headers(held).Select(h => $"{h.Key}={h.Value}"))}");
            Assert.True(waited >= AccountLockTimeout, "O cadastro deveria ter esperado a trava até o teto.");
            Assert.Equal(HttpStatusCode.Accepted, newEmail.StatusCode);
            Assert.Equal(newEmail.StatusCode, held.StatusCode);
            Assert.Equal(
                await newEmail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                await held.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(Headers(newEmail), Headers(held));
        }

        // A resposta igual só vale se veio do caminho do teto, não de uma espera que por acaso passou de 300 ms.
        Assert.Single(_logs.Entries, entry => entry.Text.Contains(GaveUpLogMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASignupThatGivesUpOnTheAccountLockWritesNothingAndSendsNoEmail()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var before = await AccountFootprintAsync(account);

        var (response, _) = await RegisterWhileHeldAsync(account, browser);
        response.Dispose();

        Assert.Single(_logs.Entries, entry => entry.Text.Contains(GaveUpLogMessage, StringComparison.Ordinal));
        Assert.Equal(before, await AccountFootprintAsync(account));
    }

    [Fact]
    public async Task ASignupThatGivesUpOnTheAccountLockIsLoggedWithoutTheEmailOrAccountId()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        var (response, _) = await RegisterWhileHeldAsync(account, browser);
        response.Dispose();

        var gaveUp = Assert.Single(_logs.Entries, entry => entry.Text.Contains(GaveUpLogMessage, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, gaveUp.Level);
        Assert.DoesNotContain(account.Email, gaveUp.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(account.UserId.ToString("D"), gaveUp.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cadastra o e-mail de <paramref name="account"/> com a linha da conta travada por outra transação.</summary>
    private async Task<(HttpResponseMessage Response, TimeSpan Waited)> RegisterWhileHeldAsync(TestAccount account, Browser browser)
    {
        var waited = Stopwatch.StartNew();
        await using (await HoldAccountAsync(account.UserId))
        {
            var signup = browser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(account.Email));
            var finished = await Task.WhenAny(signup, Task.Delay(ResponseLimit, TestContext.Current.CancellationToken));
            waited.Stop();
            Assert.True(finished == signup, $"O cadastro da conta travada não respondeu em {ResponseLimit.TotalSeconds} s.");
            return (await signup, waited.Elapsed);
        }
    }

    /// <summary>
    /// Tudo o que um cadastro grava sobre a conta: senha e stamps da linha, os tokens (orçamento de emissão e código de
    /// confirmação) e os e-mails da outbox endereçados a ela.
    /// </summary>
    private async Task<string> AccountFootprintAsync(TestAccount account)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select concat_ws(' | ',
                u.password_hash, u.security_stamp, u.concurrency_stamp,
                (select string_agg(t.login_provider || '/' || t.name || '=' || t.value, ', '
                        order by t.login_provider, t.name)
                 from identity.user_tokens t where t.user_id = u.id),
                (select string_agg(o.id::text, ', ' order by o.id) from ops.outbox_events o
                 where o.payload->>'userId' = u.id::text or lower(o.payload->>'to') = lower(u.email)))
            from identity.users u where u.id = $1
            """,
            connection);
        command.Parameters.AddWithValue(account.UserId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static SortedDictionary<string, string> Headers(HttpResponseMessage response)
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> all = response.Headers;
        HttpContentHeaders content = response.Content.Headers;
        return new SortedDictionary<string, string>(
            all.Concat(content)
                .Where(h => !PerRequestHeaders.Contains(h.Key))
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
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
