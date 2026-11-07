extern alias worker;

using System.Collections.Concurrent;
using System.Diagnostics;
using Duora.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using worker::Duora.Worker;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests;

/// <summary>
/// Um <see cref="BackgroundService"/> que termina com exceção para o host inteiro. Cada loop de fila precisa registrar
/// o erro inesperado e seguir, com a durabilidade na outbox. O relógio que explode simula um erro de programação fora
/// da lista de falhas conhecidas: cada dispatcher o lê antes de qualquer outra coisa. O heartbeat não lê o relógio; nele,
/// quem explode é a resolução da sonda, o passo que a própria sonda não tem como proteger.
/// </summary>
public sealed class WorkerLoopResilienceTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>A pausa é de 1 s; a margem cobre só a resolução do timer, nunca uma volta sem espera.</summary>
    private static readonly TimeSpan MinimumPauseAfterFailure = TimeSpan.FromMilliseconds(900);

    /// <summary>O menor intervalo do heartbeat: 30 s com 10% de jitter.</summary>
    private static readonly TimeSpan ShortestHeartbeat = TimeSpan.FromSeconds(27);

    private const string MessageCanary = "CANARY-mensagem-com-dado";

    private const string InnerCanary = "CANARY-causa-com-dado";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(SelfieVerificationProviderService))]
    [InlineData(typeof(SelfieAttemptsSuspensionService))]
    [InlineData(typeof(TransactionalEmailService))]
    [InlineData(typeof(MediaProcessingService))]
    [InlineData(typeof(ExternalAuthFlowRetentionService))]
    [InlineData(typeof(ParticipationJobService))]
    [InlineData(typeof(RetentionJobService))]
    [InlineData(typeof(HeartbeatService))]
    public async Task AnUnexpectedErrorIsLoggedWithItsFramesAndTheLoopRunsAgain(Type loopType)
    {
        var clock = new ExplodingClock();
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs);
        var loop = LoopOf(host, loopType);
        var error = logs.FirstWarningFrom(loopType.FullName!);

        await loop.StartAsync(Ct);
        await Task.WhenAny(clock.SecondPass, loop.ExecuteTask!).WaitAsync(Ceiling, Ct);

        Assert.False(loop.ExecuteTask!.IsCompleted, $"O loop terminou com {loop.ExecuteTask.Exception?.InnerException?.GetType().Name}.");
        Assert.True(clock.SecondPass.IsCompleted);
        AssertLocatesTheDefectWithoutItsContent(await error.WaitAsync(Ceiling, Ct));
        await loop.StopAsync(Ct);
    }

    /// <summary>O cancelamento que chega da parada do worker é rotina: sai do loop sem virar erro.</summary>
    [Theory]
    [InlineData(typeof(SelfieVerificationProviderService))]
    [InlineData(typeof(SelfieAttemptsSuspensionService))]
    [InlineData(typeof(TransactionalEmailService))]
    [InlineData(typeof(MediaProcessingService))]
    [InlineData(typeof(ExternalAuthFlowRetentionService))]
    [InlineData(typeof(ParticipationJobService))]
    [InlineData(typeof(RetentionJobService))]
    [InlineData(typeof(HeartbeatService))]
    public async Task ACancellationFromTheWorkerStopIsNotLoggedAsAnError(Type loopType)
    {
        var clock = new ExplodingClock();
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs);
        var loop = LoopOf(host, loopType);
        Task stopped = Task.CompletedTask;
        clock.OnRead = () =>
        {
            stopped = loop.StopAsync(CancellationToken.None);
            throw new OperationCanceledException();
        };

        await loop.StartAsync(Ct);
        await clock.FirstPass.WaitAsync(Ceiling, Ct);
        await Task.WhenAny(loop.ExecuteTask!).WaitAsync(Ceiling, Ct);
        await stopped.WaitAsync(Ceiling, Ct);

        Assert.DoesNotContain(logs.Entries, entry => entry.Category == loopType.FullName && entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AHeartbeatThatFailsWaitsTheWholeIntervalBeforeTheNextBeat()
    {
        var clock = new ExplodingClock();
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs);
        var loop = LoopOf(host, typeof(HeartbeatService));

        await loop.StartAsync(Ct);
        await Task.WhenAny(clock.SecondPass, loop.ExecuteTask!).WaitAsync(Ceiling, Ct);
        await loop.StopAsync(Ct);

        Assert.True(clock.SecondPass.IsCompleted);
        Assert.Contains(clock.RequestedTimers, wait => wait >= ShortestHeartbeat);
    }

    [Fact]
    public async Task AParticipationJobWhoseRetryAlsoFailsDoesNotStopTheLoopAndReturnsByLease()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var jobId = await SeedDueParticipationJobAsync(connectionString);
        var clock = new ExplodingClock { ExplodesOn = read => read != ClaimRead, SecondPassRead = NextClaimRead, ShortensTimers = false };
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs, connectionString);
        var loop = LoopOf(host, typeof(ParticipationJobService));

        await loop.StartAsync(Ct);
        await Task.WhenAny(clock.SecondPass, loop.ExecuteTask!).WaitAsync(Ceiling, Ct);

        Assert.False(loop.ExecuteTask!.IsCompleted, $"O loop terminou com {loop.ExecuteTask.Exception?.InnerException?.GetType().Name}.");
        var reads = clock.Reads.ToArray();
        Assert.True(reads[NextClaimRead - 1] - reads[RetryRead - 1] >= MinimumPauseAfterFailure, "Falha do retry não esperou antes do próximo claim.");
        await loop.StopAsync(Ct);
        var errors = logs.Entries.Where(entry => entry.Category == typeof(ParticipationJobService).FullName).ToArray();
        Assert.True(errors.Length >= 2, $"Só {errors.Length} erro(s) registrado(s): a conclusão e o retry falharam.");
        Assert.All(errors, AssertLocatesTheDefectWithoutItsContent);
        Assert.Equal(("InFlight", 1, (string?)null), await ReadParticipationJobAsync(connectionString, jobId));
    }

    [Fact]
    public async Task AParticipationJobThatFailsOnceIsRescheduledAndTheLoopRunsAgain()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var jobId = await SeedDueParticipationJobAsync(connectionString);
        var clock = new ExplodingClock { ExplodesOn = read => read == CompletionRead, SecondPassRead = NextClaimRead, ShortensTimers = false };
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs, connectionString);
        var loop = LoopOf(host, typeof(ParticipationJobService));

        await loop.StartAsync(Ct);
        await Task.WhenAny(clock.SecondPass, loop.ExecuteTask!).WaitAsync(Ceiling, Ct);
        await loop.StopAsync(Ct);

        Assert.True(clock.SecondPass.IsCompleted);
        Assert.Equal(("Pending", 1, "ProcessingFailed"), await ReadParticipationJobAsync(connectionString, jobId));
    }

    [Fact]
    public async Task AStopDuringTheRetryOfAParticipationJobIsNotLoggedAsAnError()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        await SeedDueParticipationJobAsync(connectionString);
        var clock = new ExplodingClock { ExplodesOn = read => read != ClaimRead, ShortensTimers = false };
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs, connectionString);
        var loop = LoopOf(host, typeof(ParticipationJobService));
        Task stopped = Task.CompletedTask;
        var reads = 0;
        clock.OnRead = () =>
        {
            if (++reads == RetryRead)
            {
                stopped = loop.StopAsync(CancellationToken.None);
                throw new OperationCanceledException();
            }
        };

        await loop.StartAsync(Ct);
        await Task.WhenAny(loop.ExecuteTask!).WaitAsync(Ceiling, Ct);
        await stopped.WaitAsync(Ceiling, Ct);

        var entry = Assert.Single(logs.Entries, entry => entry.Category == typeof(ParticipationJobService).FullName && entry.Level >= LogLevel.Warning);
        AssertLocatesTheDefectWithoutItsContent(entry);
    }

    /// <summary>Um erro de banco na parada não é erro de programação: fica em Warning, e o loop sai pela espera cancelada.</summary>
    [Fact]
    public async Task ADatabaseFailureDuringTheStopStaysAWarningAndTheLoopEndsCleanly()
    {
        var clock = new ExplodingClock();
        var logs = new CapturingLoggerProvider();
        using var host = BuildWorker(clock, logs);
        var loop = LoopOf(host, typeof(ParticipationJobService));
        Task stopped = Task.CompletedTask;
        clock.OnRead = () =>
        {
            stopped = loop.StopAsync(CancellationToken.None);
            throw new NpgsqlException(MessageCanary);
        };

        await loop.StartAsync(Ct);
        await clock.FirstPass.WaitAsync(Ceiling, Ct);
        await Task.WhenAny(loop.ExecuteTask!).WaitAsync(Ceiling, Ct);
        await stopped.WaitAsync(Ceiling, Ct);

        Assert.False(loop.ExecuteTask!.IsFaulted, $"O loop terminou com {loop.ExecuteTask.Exception?.InnerException?.GetType().Name}.");
        var entry = Assert.Single(logs.Entries, entry => entry.Category == typeof(ParticipationJobService).FullName && entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(MessageCanary, entry.Text, StringComparison.Ordinal);
    }

    private static void AssertLocatesTheDefectWithoutItsContent(CapturedLog entry)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(ExplodingClockException), entry.Text, StringComparison.Ordinal);
        Assert.Contains($"{nameof(ExplodingClock)}.{nameof(ExplodingClock.GetUtcNow)}:line ", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Duora.", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageCanary, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(InnerCanary, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", entry.Text, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    private static async Task<Guid> SeedDueParticipationJobAsync(string connectionString)
    {
        var jobId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            insert into ops.jobs(id, kind, aggregate_id, due_at, available_at)
            values ($1, 'close-round', $2, now() - interval '1 minute', now() - interval '1 minute')
            """,
            connection);
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        return jobId;
    }

    private static async Task<(string Status, int Attempts, string? LastError)> ReadParticipationJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select status, attempts, last_error_code from ops.jobs where id = $1", connection);
        command.Parameters.AddWithValue(jobId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static BackgroundService LoopOf(IHost host, Type loopType) =>
        (BackgroundService)Assert.Single(host.Services.GetServices<IHostedService>(), service => service.GetType() == loopType);

    private IHost BuildWorker(ExplodingClock clock, CapturingLoggerProvider logs, string? connectionString = null)
    {
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={connectionString ?? postgres.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test",
            "--Retention:Enabled=true",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. MediaTestArguments,
            .. SelfieArguments,
        ]);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<DatabaseProbe>(_ => clock.ExplodeInsteadOfProbing());
        builder.Logging.AddProvider(logs);
        return builder.Build();
    }

    private static string[] SelfieArguments => [.. Identity.SelfieTestSettings.Enabled().Select(setting => $"--{setting.Key}={setting.Value}")];

    private static string[] MediaTestArguments =>
    [
        "--Media:Review=TechnicalOnly",
        "--Media:Storage:Provider=FileSystem",
        $"--Media:Storage:FileSystemRoot={Path.Combine(Path.GetTempPath(), "duora-media-unused")}",
        $"--Media:Deletion:EnvironmentId={Guid.NewGuid()}",
        "--Media:Deletion:OperationKeyId=test-v1",
        $"--Media:Deletion:OperationKey={Convert.ToBase64String(new byte[32])}",
        "--Media:Processing:Sanitizer:LauncherSocket=/tmp/duora-launcher-unused.sock",
        "--Media:Processing:Sanitizer:LauncherUid=1000",
        $"--Media:Processing:Sanitizer:ProfileSha256={new string('a', 64)}",
    ];

    // Leituras do relógio numa volta do ParticipationJobService com um job vencido.
    private const int ClaimRead = 1;
    private const int CompletionRead = 2;
    private const int RetryRead = 3;
    private const int NextClaimRead = 4;

    /// <summary>
    /// Cada volta dos loops lê o relógio uma vez, e essa leitura explode: a segunda leitura prova a segunda volta. Os
    /// timers encurtados poupam a espera entre voltas, que na limpeza OIDC é de 1 minuto. O job de participação liga o
    /// prazo da conclusão a este relógio, então os testes dele desligam o encurtamento e escolhem qual leitura explode.
    /// </summary>
    private sealed class ExplodingClock : TimeProvider
    {
        private readonly TaskCompletionSource _firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TimeSpan> _reads = new();
        private readonly ConcurrentQueue<TimeSpan> _requestedTimers = new();

        public Task FirstPass => _firstPass.Task;

        public Task SecondPass => _secondPass.Task;

        public IReadOnlyCollection<TimeSpan> Reads => _reads;

        public IReadOnlyCollection<TimeSpan> RequestedTimers => _requestedTimers;

        public Func<int, bool> ExplodesOn { get; init; } = _ => true;

        public int SecondPassRead { get; init; } = 2;

        public bool ShortensTimers { get; init; } = true;

        public Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            _reads.Enqueue(_elapsed.Elapsed);
            var reads = _reads.Count;
            _firstPass.TrySetResult();
            if (reads >= SecondPassRead)
            {
                _secondPass.TrySetResult();
            }

            OnRead?.Invoke();
            return ExplodesOn(reads) ? throw new ExplodingClockException() : base.GetUtcNow();
        }

        public DatabaseProbe ExplodeInsteadOfProbing()
        {
            GetUtcNow();
            throw new UnreachableException();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _requestedTimers.Enqueue(dueTime);
            return base.CreateTimer(callback, state, ShortensTimers ? TimeSpan.FromMilliseconds(1) : dueTime, period);
        }
    }

    private sealed class ExplodingClockException() : Exception(MessageCanary, new InvalidOperationException(InnerCanary));
}
