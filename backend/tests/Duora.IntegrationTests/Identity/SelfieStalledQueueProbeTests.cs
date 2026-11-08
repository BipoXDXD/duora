using System.Text.RegularExpressions;
using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// A consulta de fila parada de <c>scripts/deploy/selfie_provider_alert.py</c>, contra o schema migrado. O texto vem do
/// próprio script. Cada leitura roda na transação que prepara os dados: o <c>now()</c> fica fixo nela, e os limites são
/// exatos, sem relógio de verdade.
/// </summary>
public sealed class SelfieStalledQueueProbeTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    [Theory]
    [InlineData(60_000, true)]
    [InlineData(59_999, false)]
    public async Task APendingDeliveryCountsFromTheLimit(int waitingMilliseconds, bool stalled)
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.Equal(stalled, await ProbeAsync(connection,
            "update ops.outbox_deliveries set available_at = now() - $1 where consumer = 'selfie-provider'",
            TimeSpan.FromMilliseconds(waitingMilliseconds)));
    }

    [Theory]
    [InlineData(60_000, true)]
    [InlineData(59_999, false)]
    public async Task AnExpiredLeaseCountsFromTheLimit(int expiredMilliseconds, bool stalled)
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.Equal(stalled, await ProbeAsync(connection,
            "update ops.outbox_deliveries set status = 'InFlight', lease_expires_at = now() - $1 where consumer = 'selfie-provider'",
            TimeSpan.FromMilliseconds(expiredMilliseconds)));
    }

    /// <summary>Lease vivo é um worker com trabalho na mão: a fila longa com fornecedor lento não está parada.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ALiveLeaseMeansTheQueueIsMoving(int leaseLeftMilliseconds, bool stalled)
    {
        var connection = await QueueAsync(deliveries: 2);

        Assert.Equal(stalled, await ProbeAsync(connection, """
            update ops.outbox_deliveries set available_at = now() - interval '61 seconds'
            where consumer = 'selfie-provider';
            update ops.outbox_deliveries set status = 'InFlight', lease_expires_at = now() + $1
            where id = (select id from ops.outbox_deliveries where consumer = 'selfie-provider' order by id limit 1)
            """, TimeSpan.FromMilliseconds(leaseLeftMilliseconds)));
    }

    /// <summary>Lease vivo de outra fila, como a de e-mail, não diz nada sobre a selfie.</summary>
    [Fact]
    public async Task ALiveLeaseOfAnotherConsumerDoesNotHideAStalledQueue()
    {
        var connection = await QueueAsync(deliveries: 2);

        Assert.True(await ProbeAsync(connection, """
            update ops.outbox_deliveries set available_at = now() - interval '61 seconds'
            where consumer = 'selfie-provider';
            update ops.outbox_deliveries set consumer = 'another-consumer', status = 'InFlight',
                lease_expires_at = now() + interval '30 seconds'
            where id = (select id from ops.outbox_deliveries where consumer = 'selfie-provider' order by id limit 1)
            """));
    }

    [Fact]
    public async Task AnExpiredLeaseOfAnotherConsumerIsNotSelfieWork()
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.False(await ProbeAsync(connection, """
            update ops.outbox_deliveries set consumer = 'another-consumer', status = 'InFlight',
                lease_expires_at = now() - interval '61 seconds'
            where consumer = 'selfie-provider'
            """));
    }

    [Fact]
    public async Task AnEmptyQueueIsNotStalled()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);

        Assert.False(await ProbeAsync(connection, "select 1"));
    }

    [Fact]
    public async Task ADeliveryScheduledForLaterIsNotWaiting()
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.False(await ProbeAsync(connection,
            "update ops.outbox_deliveries set available_at = now() + interval '5 seconds' where consumer = 'selfie-provider'"));
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    public async Task AFinishedDeliveryIsNotWaiting(string status)
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.False(await ProbeAsync(connection,
            "update ops.outbox_deliveries set status = $1, available_at = now() - interval '61 seconds' where consumer = 'selfie-provider'",
            status));
    }

    [Fact]
    public async Task AnotherConsumerIsIgnored()
    {
        var connection = await QueueAsync(deliveries: 1);

        Assert.False(await ProbeAsync(connection,
            "update ops.outbox_deliveries set consumer = 'another-consumer', available_at = now() - interval '61 seconds' where consumer = 'selfie-provider'"));
    }

    /// <summary>
    /// O caso que motivou a sonda, com o dispatcher real: a chamada cortada que ignora o cancelamento segura a vaga, a fila
    /// para e nenhum marcador novo é gravado. Quando a chamada termina, o worker volta a andar e a sonda se cala.
    /// </summary>
    [Fact]
    public async Task ASlotHeldByAnAbandonedCallIsSeenAndClearsWhenTheCallEnds()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new StartThatIgnoresTheCut(time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, time, provider);
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        var batch = SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        await provider.Entered.WaitAsync(Ceiling, Ct);
        time.Advance(TimeSpan.FromSeconds(10));
        await batch;
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(0, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));
        Assert.Equal(1, await scenario.ScalarAsync("""
            update ops.outbox_deliveries set available_at = now() - interval '61 seconds'
            where consumer = 'selfie-provider' and status = 'Pending'
            returning 1
            """));

        Assert.True(await ProbeAsync(connection, "select 1"));

        provider.Release();
        await provider.FirstCall!.WaitAsync(Ceiling, Ct);
        Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(host, Ct));

        Assert.False(await ProbeAsync(connection, "select 1"));
    }

    /// <summary>A consulta que o script realmente envia: uma linha, as duas respostas, na ordem que ele lê.</summary>
    [Fact]
    public async Task TheCombinedReadingAnswersBothQuestions()
    {
        var connection = await QueueAsync(deliveries: 1);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync(Ct);
        await using var transaction = await database.BeginTransactionAsync(Ct);
        await using var age = new NpgsqlCommand(
            "update ops.outbox_deliveries set available_at = now() - interval '61 seconds' where consumer = 'selfie-provider'",
            database, transaction);
        await age.ExecuteNonQueryAsync(Ct);

        await using var reading = new NpgsqlCommand(CombinedQuery(), database, transaction);
        await using var row = await reading.ExecuteReaderAsync(Ct);

        Assert.True(await row.ReadAsync(Ct));
        Assert.Equal((false, true), (row.GetBoolean(0), row.GetBoolean(1)));
        Assert.False(await row.ReadAsync(Ct));
    }

    private async Task<string> QueueAsync(int deliveries)
    {
        var time = new FakeTimeProvider(Now);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = SelfieConcurrencyHarness.Host(connection, time, new SimulatedSelfieProvider(time));
        var scenario = new IdentityScenario(connection, host);
        for (var delivery = 0; delivery < deliveries; delivery++)
        {
            var account = await scenario.CreateConfirmedAccountAsync();
            await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);
        }

        return connection;
    }

    private static async Task<bool> ProbeAsync(string connectionString, string arrange, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var statement in arrange.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var change = new NpgsqlCommand(statement, connection, transaction);
            if (statement.Contains("$1", StringComparison.Ordinal))
            {
                foreach (var parameter in parameters)
                {
                    change.Parameters.AddWithValue(parameter);
                }
            }

            await change.ExecuteNonQueryAsync(Ct);
        }

        await using var probe = new NpgsqlCommand(StalledQuery(), connection, transaction);
        return Assert.IsType<bool>(await probe.ExecuteScalarAsync(Ct));
    }

    private static string StalledQuery() => ScriptQuery("STALLED_QUERY");

    private static string CombinedQuery()
    {
        var composed = Assert.Single(Regex.Matches(Script(), "QUEUE_QUERY = f'(.*)'")).Groups[1].Value;
        return composed.Replace("{PROBE_QUERY}", ScriptQuery("PROBE_QUERY"), StringComparison.Ordinal)
            .Replace("{STALLED_QUERY}", StalledQuery(), StringComparison.Ordinal);
    }

    private static string ScriptQuery(string name) =>
        Assert.Single(Regex.Matches(Script(), name + " = \"\"\"(.*?)\"\"\"", RegexOptions.Singleline)).Groups[1].Value;

    private static string Script() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "scripts", "deploy", "selfie_provider_alert.py"));

    /// <summary>A primeira abertura de sessão fica presa e ignora o token, até o teste liberar.</summary>
    private sealed class StartThatIgnoresTheCut(FakeTimeProvider time) : ISelfieProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<SelfieSession>? FirstCall { get; private set; }

        public void Release() => _release.TrySetResult();

        public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            if (FirstCall is not null)
            {
                return Task.FromResult(Session(attemptId));
            }

            _entered.TrySetResult();
            FirstCall = HoldAsync(attemptId);
            return FirstCall;
        }

        public Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(new SelfieResult(SelfieState.Processing, null, "synthetic-policy"));

        private async Task<SelfieSession> HoldAsync(Guid attemptId)
        {
            await _release.Task;
            return Session(attemptId);
        }

        private SelfieSession Session(Guid attemptId) => new(attemptId.ToString("D"), time.GetUtcNow().AddMinutes(3));
    }
}
