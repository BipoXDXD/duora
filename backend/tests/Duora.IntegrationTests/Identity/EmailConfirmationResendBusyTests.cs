using System.Diagnostics;
using System.Text.RegularExpressions;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #470: o reenvio da confirmação (API-A03) não pode dizer pelo status se o e-mail tem conta. Só uma conta
/// existente espera o lock da linha no PostgreSQL (55P03); o e-mail inexistente sai antes e sempre recebe 202. Se a
/// conta ocupada respondesse 503, o status viraria oráculo de existência (TM-06).
/// </summary>
[Trait("Threat", "TM-06")]
[Collection(nameof(ResendLockSerial))]
public sealed partial class EmailConfirmationResendBusyTests(PostgresFixture postgres)
{
    private const string Requests = "/api/v1/auth/email-confirmation-requests";

    /// <summary>Teto de espera dos sinais entre o teste e a transação que segura a linha.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Teto da fila em memória nestes testes. Se o reenvio voltasse a esperar a fila pelo teto do login, a conta com a
    /// vez ocupada levaria isso tudo, bem acima de <see cref="HeldTurnLimit"/>.
    /// </summary>
    private const string LoginQueueTimeout = "00:00:02";

    /// <summary>
    /// O piso com o jitter máximo (600 ms) e folga para a máquina carregada; muito abaixo de
    /// <see cref="LoginQueueTimeout"/>.
    /// </summary>
    private static readonly TimeSpan HeldTurnLimit =
        NeutralResponseFloor.Floor + NeutralResponseFloor.MaxJitter + TimeSpan.FromMilliseconds(500);

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

    /// <summary>
    /// Contrato do reenvio (#470): conta existente livre, e-mail inexistente e conta ocupada recebem a mesma resposta
    /// inteira, 202 sem corpo. Antes do #470 a conta ocupada recebia o 503 com <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public async Task ExistingMissingAndBusyAccountsGetTheSameEmptyAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = ShortLockFactory();
        var pending = await RegisterPendingAsync(factory);

        using var existing = await ResendAsync(factory, pending.Email);
        var existingAccount = await DescribeAsync(existing, ct);
        using var missing = await ResendAsync(factory, IdentityScenario.NewEmail());
        var missingEmail = await DescribeAsync(missing, ct);
        string busyAccount;
        await using (await RowHolder.HoldAsync(postgres.ConnectionString, pending.UserId))
        {
            using var busy = await ResendAsync(factory, pending.Email);
            busyAccount = await DescribeAsync(busy, ct);
        }

        Assert.StartsWith("202||", missingEmail, StringComparison.Ordinal);
        Assert.Equal(missingEmail, existingAccount);
        Assert.Equal(missingEmail, busyAccount);
    }

    /// <summary>
    /// A vez da conta na fila em memória também é espera curta: com outra verificação segurando a vez, o reenvio
    /// desiste dentro do piso, sem e-mail, e responde como o e-mail inexistente. Se a fila esperasse o teto do login
    /// (<c>Identity:AccountQueueTimeout</c>), a demora diria que a conta existe.
    /// </summary>
    [Fact]
    public async Task APendingAccountWhoseTurnIsHeldAnswersLikeAMissingEmailWithinTheFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = ShortLockFactory();
        var scenario = new IdentityScenario(postgres, factory);
        var pending = await RegisterPendingAsync(factory);
        var emailsBefore = await scenario.CountOutboxEventsAsync(pending.Email);
        var gate = factory.Services.GetRequiredService<AccountCheckGate>();

        string heldTurn;
        TimeSpan elapsed;
        await using (await gate.EnterAsync(pending.UserId, Patience, ct))
        {
            var startedAt = Stopwatch.GetTimestamp();
            using var response = await ResendAsync(factory, pending.Email);
            elapsed = Stopwatch.GetElapsedTime(startedAt);
            heldTurn = await DescribeAsync(response, ct);
        }
        using var missing = await ResendAsync(factory, IdentityScenario.NewEmail());

        Assert.Equal(await DescribeAsync(missing, ct), heldTurn);
        Assert.Equal(emailsBefore, await scenario.CountOutboxEventsAsync(pending.Email));
        Assert.InRange(elapsed, TimeSpan.Zero, HeldTurnLimit);
    }

    /// <summary>
    /// O 202 da conta travada não pode esconder um envio: o reenvio desiste sem gravar nada, nem enquanto a linha
    /// está presa nem depois que ela é solta.
    /// </summary>
    [Fact]
    public async Task ALockedPendingAccountGetsNoEmail()
    {
        await using var factory = ShortLockFactory();
        var scenario = new IdentityScenario(postgres, factory);
        var pending = await RegisterPendingAsync(factory);
        var emailsBefore = await scenario.CountOutboxEventsAsync(pending.Email);

        long emailsWhileLocked;
        await using (await RowHolder.HoldAsync(postgres.ConnectionString, pending.UserId))
        {
            using var response = await ResendAsync(factory, pending.Email);
            emailsWhileLocked = await scenario.CountOutboxEventsAsync(pending.Email);
        }

        Assert.Equal(emailsBefore, emailsWhileLocked);
        Assert.Equal(emailsBefore, await scenario.CountOutboxEventsAsync(pending.Email));
    }

    /// <summary>
    /// A desistência não gasta nada da conta: nem o orçamento persistente de emissões (3 por hora, já com 1 do
    /// cadastro), nem o stamp, nem o código. Duas desistências seguidas e, solta a linha, o reenvio ainda emite.
    /// </summary>
    [Fact]
    public async Task AResendThatGaveUpOnALockedAccountSpendsNothingFromTheNextOne()
    {
        await using var factory = ShortLockFactory();
        var scenario = new IdentityScenario(postgres, factory);
        var pending = await RegisterPendingAsync(factory);
        await using (await RowHolder.HoldAsync(postgres.ConnectionString, pending.UserId))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var gaveUp = await ResendAsync(factory, pending.Email);
            }
        }
        var emailsBefore = await scenario.CountOutboxEventsAsync(pending.Email);

        using var released = await ResendAsync(factory, pending.Email);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, released.StatusCode);
        Assert.Equal(emailsBefore + 1, await scenario.CountOutboxEventsAsync(pending.Email));
    }

    /// <summary>
    /// Os outros desfechos do reenvio também saem idênticos ao e-mail inexistente: pendente que recebe código,
    /// conta já confirmada e pendente com o orçamento de emissões esgotado. A outbox confirma qual desfecho foi.
    /// </summary>
    [Theory]
    [InlineData("pending", 1)]
    [InlineData("confirmed", 0)]
    [InlineData("budget-exhausted", 0)]
    public async Task EveryResendOutcomeAnswersLikeAMissingEmail(string kind, int emailsSent)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = ShortLockFactory();
        var scenario = new IdentityScenario(postgres, factory);
        var email = kind == "confirmed"
            ? (await scenario.CreateConfirmedAccountAsync()).Email
            : (await RegisterPendingAsync(factory)).Email;
        if (kind == "budget-exhausted")
        {
            // O cadastro gastou 1 das 3 emissões por hora; mais 2 reenvios esgotam o orçamento.
            for (var resend = 0; resend < 2; resend++)
            {
                using var spent = await ResendAsync(factory, email);
            }
        }

        var emailsBefore = await scenario.CountOutboxEventsAsync(email);

        using var outcome = await ResendAsync(factory, email);
        var account = await DescribeAsync(outcome, ct);
        using var missing = await ResendAsync(factory, IdentityScenario.NewEmail());

        Assert.Equal(await DescribeAsync(missing, ct), account);
        Assert.Equal(emailsBefore + emailsSent, await scenario.CountOutboxEventsAsync(email));
    }

    /// <summary>
    /// O motivo e o teto que estourou ficam só no log, em Warning, sem o e-mail: o cliente recebe o 202 de sempre.
    /// Com a linha presa, a fila passa e quem desiste é o lock, no teto curto padrão de 100 ms.
    /// </summary>
    [Fact]
    public async Task ALockedPendingAccountLeavesAWarningWithoutTheEmail()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = ShortLockFactory(logs);
        var pending = await RegisterPendingAsync(factory);
        var category = typeof(Duora.Web.Accounts.AuthEndpoints).FullName!;

        await using (await RowHolder.HoldAsync(postgres.ConnectionString, pending.UserId))
        {
            using var response = await ResendAsync(factory, pending.Email);
        }

        var warning = await RowHolder.WithinPatienceAsync(
            logs.FirstWarningFrom(category), "O reenvio com a conta ocupada não deixou Warning.");
        Assert.Contains("account busy (AccountLock), ceiling 100 ms", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(pending.Email, warning.Text, StringComparison.OrdinalIgnoreCase);
    }

    private DuoraWebFactory ShortLockFactory(CapturingLoggerProvider? logs = null) =>
        new(postgres.ConnectionString, logs)
        {
            // O reenvio espera só Identity:ShortWait. Os tetos do login ficam curtos para que, se o reenvio voltar a
            // usá-los, estes testes continuem rápidos; a volta ao teto do lock é pega por
            // EmailConfirmationResendTimingTests, e a do teto da fila, pelo teste da vez ocupada.
            Settings = new Dictionary<string, string?>
            {
                ["Identity:AccountLockTimeout"] = "00:00:00.300",
                ["Identity:AccountQueueTimeout"] = LoginQueueTimeout,
                // Os limites em memória responderiam 429 antes do caminho testado nos reenvios repetidos.
                ["RateLimiting:RegisterPerIp:PermitLimit"] = "1000",
                ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1000",
            },
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

        public static async Task WithinPatienceAsync(Task task, string message)
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

        public static async Task<T> WithinPatienceAsync<T>(Task<T> task, string message)
        {
            await WithinPatienceAsync((Task)task, message);
            return await task;
        }
    }
}
