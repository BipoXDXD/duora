using System.Text.RegularExpressions;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #470: o reenvio da confirmação (API-A03) não pode dizer pelo status se o e-mail tem conta. Só uma conta
/// existente espera o lock da linha no PostgreSQL (55P03); o e-mail inexistente sai antes e sempre recebe 202. Se a
/// conta ocupada respondesse 503, o status viraria oráculo de existência (TM-06).
/// </summary>
[Trait("Threat", "TM-06")]
public sealed partial class EmailConfirmationResendBusyTests(PostgresFixture postgres)
{
    private const string Requests = "/api/v1/auth/email-confirmation-requests";

    /// <summary>Teto de espera dos sinais entre o teste e a transação que segura a linha.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ALockedPendingAccountAnswersLikeAMissingEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = ShortLockFactory();
        var pending = await RegisterPendingAsync(factory);

        string lockedAccount;
        await using (await RowHolder.HoldAsync(postgres.ConnectionString, pending.UserId))
        {
            using var response = await ResendAsync(factory, pending.Email);
            lockedAccount = await DescribeAsync(response, ct);
        }

        using var missing = await ResendAsync(factory, IdentityScenario.NewEmail());
        var missingEmail = await DescribeAsync(missing, ct);

        Assert.Equal(missingEmail, lockedAccount);
        Assert.StartsWith("202|", missingEmail, StringComparison.Ordinal);
    }

    private DuoraWebFactory ShortLockFactory() =>
        new(postgres.ConnectionString)
        {
            // Teto curto para o 55P03 chegar logo; o padrão (5 s) só deixaria o teste mais lento.
            Settings = new Dictionary<string, string?> { ["Identity:AccountLockTimeout"] = "00:00:00.300" },
        };

    /// <summary>Cadastro pendente com a senha certa: sem a trava, o reenvio emitiria um código novo.</summary>
    private async Task<(Guid UserId, string Email)> RegisterPendingAsync(DuoraWebFactory factory)
    {
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, registered.StatusCode);
        var link = await new IdentityScenario(postgres, factory).ReadLatestLinkAsync(email, "EmailConfirmation");
        return (link.UserId, email);
    }

    private static async Task<HttpResponseMessage> ResendAsync(DuoraWebFactory factory, string email)
    {
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        return await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
    }

    /// <summary>Status, corpo sem <c>traceId</c> e todos os headers, exceto <c>Date</c> e <c>Set-Cookie</c>.</summary>
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

    /// <summary>
    /// Outra transação com a linha da conta travada (<c>FOR UPDATE</c>), como o webhook de idade, a selfie ou a
    /// suspensão. Roda à parte e avisa por sinal quando já tem a linha; solta quando o teste descarta.
    /// </summary>
    private sealed class RowHolder : IAsyncDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _holding = Task.CompletedTask;

        public static async Task<RowHolder> HoldAsync(string connectionString, Guid userId)
        {
            var holder = new RowHolder();
            holder._holding = holder.HoldUntilReleasedAsync(connectionString, userId);
            await WithinPatienceAsync(holder._held.Task, "A outra transação não travou a linha da conta.");
            return holder;
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            await WithinPatienceAsync(_holding, "A outra transação não soltou a linha da conta.");
        }

        private async Task HoldUntilReleasedAsync(string connectionString, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var command = new NpgsqlCommand(
                    "select 1 from identity.users where id = $1 for update", connection, transaction))
                {
                    command.Parameters.AddWithValue(userId);
                    await command.ExecuteNonQueryAsync();
                }

                _held.TrySetResult();
                await _release.Task;
                await transaction.RollbackAsync();
            }
            catch (Exception error)
            {
                _held.TrySetException(error);
                throw;
            }
        }

        private static async Task WithinPatienceAsync(Task task, string message)
        {
            try
            {
                await task.WaitAsync(Patience);
            }
            catch (TimeoutException timeout)
            {
                throw new TimeoutException($"{message} Esperou {Patience.TotalSeconds} s.", timeout);
            }
        }
    }
}
