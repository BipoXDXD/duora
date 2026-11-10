using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
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

    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _factory;

    public RegistrationLockTimeoutTests(PostgresFixture postgres)
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
    public async Task ASignupOfAHeldAccountGivesUpAtTheCeilingWithTheSameResponseAsANewEmail()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var newEmailBrowser = new Browser(_factory);
        await newEmailBrowser.FetchCsrfTokenAsync();
        using var newEmail = await newEmailBrowser.PostJsonAsync(
            "/api/v1/auth/register", IdentityScenario.RegistrationBody(IdentityScenario.NewEmail()));
        using var heldBrowser = new Browser(_factory);
        await heldBrowser.FetchCsrfTokenAsync();

        HttpResponseMessage held;
        var waited = Stopwatch.StartNew();
        await using (await HoldAccountAsync(account.UserId))
        {
            var signup = heldBrowser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(account.Email));
            var finished = await Task.WhenAny(signup, Task.Delay(ResponseLimit, TestContext.Current.CancellationToken));
            waited.Stop();
            Assert.True(finished == signup, $"O cadastro da conta travada não respondeu em {ResponseLimit.TotalSeconds} s.");
            held = await signup;
        }

        using (held)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"#461 cadastro da conta travada: {(int)held.StatusCode} em {waited.ElapsedMilliseconds} ms; " +
                $"headers={string.Join("; ", Headers(held).Select(h => $"{h.Key}={h.Value}"))}");
            Assert.True(waited.Elapsed >= AccountLockTimeout, "O cadastro deveria ter esperado a trava até o teto.");
            Assert.Equal(newEmail.StatusCode, held.StatusCode);
            Assert.Equal(
                await newEmail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                await held.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(Headers(newEmail), Headers(held));
        }
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
