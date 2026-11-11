using System.Text.RegularExpressions;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #468: o <c>503 TEMPORARILY_UNAVAILABLE</c> do login não pode dizer se a conta existe. Só uma conta
/// existente espera o lock da linha no PostgreSQL (55P03); se o <c>Retry-After</c> desse caso fosse o teto do lock,
/// diferente do que um e-mail inexistente recebe na fila ou no prazo, o header viraria oráculo de existência (TM-06).
/// </summary>
[Trait("Threat", "TM-06")]
public sealed partial class LoginBusyNeutralityTests(PostgresFixture postgres)
{
    private const string WrongPassword = "senha incorreta longa";

    /// <summary>
    /// Os tetos são curtos e distintos de propósito: com o mesmo valor arredondado, o teste não veria a diferença.
    /// "queue": o e-mail inexistente desiste na fila por e-mail (2 s); "deadline": no prazo total do login (1,5 s),
    /// antes da fila (3 s). Nos dois, a conta existente desiste no lock da linha (300 ms).
    /// </summary>
    [Theory]
    [InlineData("queue")]
    [InlineData("deadline")]
    public async Task ABusyLockedAccountAnswersLikeABusyMissingEmail(string missingEmailGivesUpAt)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = missingEmailGivesUpAt == "queue"
                ? new Dictionary<string, string?>
                {
                    ["Identity:AccountLockTimeout"] = "00:00:00.300",
                    ["Identity:AccountQueueTimeout"] = "00:00:02",
                }
                : new Dictionary<string, string?>
                {
                    ["Identity:AccountLockTimeout"] = "00:00:00.300",
                    ["Identity:AccountQueueTimeout"] = "00:00:03",
                    ["Identity:LoginTimeout"] = "00:00:01.500",
                },
        };
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();

        string lockedAccount;
        await using (await HoldAccountRowAsync(account.UserId))
        {
            using var browser = new Browser(factory);
            using var response = await IdentityScenario.LoginAsync(browser, account.Email, WrongPassword);
            lockedAccount = await DescribeAsync(response, ct);
        }

        var missing = IdentityScenario.NewEmail();
        Assert.True(EmailAddress.TryParse(missing, out var address));
        string missingEmail;
        using (var scope = factory.Services.CreateScope())
        await using (await scope.ServiceProvider.GetRequiredService<AccountSecurityService>()
            .EnterLoginAsync(address, ct))
        {
            using var browser = new Browser(factory);
            using var response = await IdentityScenario.LoginAsync(browser, missing, WrongPassword);
            missingEmail = await DescribeAsync(response, ct);
        }

        Assert.StartsWith("503|", lockedAccount, StringComparison.Ordinal);
        Assert.Contains("Retry-After=", lockedAccount, StringComparison.Ordinal);
        Assert.Equal(missingEmail, lockedAccount);
    }

    /// <summary>
    /// Banco indisponível (pool esgotado) é outro contrato, mas também não pode depender da existência da conta. O
    /// valor do <c>Retry-After</c> não é fixado aqui: ele é do handler de banco indisponível; aqui importa só que
    /// exista e seja igual nas duas respostas.
    /// </summary>
    [Fact]
    public async Task AnUnavailableDatabaseAnswersTheSameForAnExistingAndAMissingEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Database:MaxPoolSize"] = "1",
                ["Database:PoolTimeout"] = "00:00:01",
            },
        };
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        using var existingBrowser = new Browser(factory);
        await existingBrowser.FetchCsrfTokenAsync();
        using var missingBrowser = new Browser(factory);
        await missingBrowser.FetchCsrfTokenAsync();

        await using var onlyConnection =
            await factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        using var existing = await IdentityScenario.LoginAsync(existingBrowser, account.Email, WrongPassword);
        using var missing =
            await IdentityScenario.LoginAsync(missingBrowser, IdentityScenario.NewEmail(), WrongPassword);
        var existingEmail = await DescribeAsync(existing, ct);

        Assert.StartsWith("503|", existingEmail, StringComparison.Ordinal);
        Assert.True(ProblemAssert.RetryAfterSeconds(existing) > 0);
        Assert.Equal(await DescribeAsync(missing, ct), existingEmail);
    }

    /// <summary>
    /// Status, tipo, corpo sem <c>traceId</c> e todos os headers, exceto <c>Date</c> e <c>Set-Cookie</c>.
    /// </summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(ct), string.Empty);
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Set-Cookie"))
            .Select(header => $"{header.Key}={string.Join(',', header.Value)}")
            .Order(StringComparer.Ordinal);
        return $"{(int)response.StatusCode}|{body}|{string.Join(';', headers)}";
    }

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();

    /// <summary>Outra transação com a linha da conta travada, como o webhook de idade ou a suspensão.</summary>
    private async Task<RowHolder> HoldAccountRowAsync(Guid userId)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(
            "select 1 from identity.users where id = $1 for no key update", connection, transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
        return new RowHolder(connection, transaction);
    }

    private sealed class RowHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
