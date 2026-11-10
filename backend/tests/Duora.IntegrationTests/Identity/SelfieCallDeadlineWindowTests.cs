using System.Collections.Concurrent;
using System.Diagnostics;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Issue #453: o corte de 10 s não pode depender do instante em que a chamada ao fornecedor devolve a tarefa. O teste
/// leva o relógio até o corte no pior momento, sem sorte nem espera: logo depois da primeira leitura de hora que o
/// dispatcher faz após a chamada, ou, se ele não lê a hora, com o lote já parado esperando. Nos dois casos o lote tem de
/// encerrar sem nenhum outro avanço do relógio, mesmo com um fornecedor que ignora o cancelamento.
/// </summary>
public sealed class SelfieCallDeadlineWindowTests(PostgresFixture postgres)
{
    /// <summary>O corte do dispatcher, no relógio do worker.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Teto em tempo real para o lote encerrar depois do corte: falha em vez de pendurar a suíte.</summary>
    private static readonly TimeSpan BatchCeiling = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCutComesEvenWhenTheClockReachesItRightAfterTheDispatcherReadsTheTime()
    {
        var clock = new CutWindowClock(new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var provider = new StartThatIgnoresTheCut(clock);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        await using var host = new DuoraWebFactory(connection)
        {
            Time = clock,
            Settings = SelfieTestSettings.Enabled("synthetic-policy"),
            TestServices = services =>
            {
                services.RemoveAll<ISelfieProvider>();
                services.AddSingleton<ISelfieProvider>(provider);
            },
        };
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SelfieConcurrencyHarness.RequestAsync(host, account.UserId, Ct);

        try
        {
            using var scope = host.Services.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>();
            using var pump = new SingleThreadPump();
            var batch = pump.Start(() => dispatcher.DispatchBatchAsync(Ct));

            pump.RunUntilCompleted(batch, BatchCeiling, clock.ReachTheCutIfTheBatchIsParked, () => clock.CutReached);

            Assert.Equal(1, await batch);
        }
        finally
        {
            provider.Release();
        }

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal("Pending|no-reference|PROVIDER_UNAVAILABLE|open", await scenario.ScalarTextAsync("""
            select state
                || case when provider_reference is null then '|no-reference' else '|has-reference' end
                || '|' || coalesce(feedback_code, 'no-feedback')
                || case when completed_at is null then '|open' else '|closed' end
            from identity.selfie_verifications
            """));
        Assert.Equal("Pending|ProviderUnavailable|1", await scenario.ScalarTextAsync("""
            select status || '|' || coalesce(last_error, '-') || '|' || attempts
            from ops.outbox_deliveries
            where consumer = 'selfie-provider'
            """));
    }

    /// <summary>
    /// Relógio do teste que leva o tempo até o corte uma única vez, depois da chamada. Se o dispatcher lê a hora na thread
    /// da chamada, o avanço vem logo depois dessa leitura, que devolve a hora de antes: é a janela da issue #453, entre ler
    /// o prazo e armar o timer. Os timers vencidos disparam dentro do avanço, como no <see cref="FakeTimeProvider"/>.
    /// </summary>
    private sealed class CutWindowClock(FakeTimeProvider inner) : TimeProvider
    {
        private const int Disarmed = 0;
        private int _armedThread = Disarmed;

        public string CutReached { get; private set; } = "o relógio ainda não chegou ao corte";

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public void ReachTheCutAtTheNextReadOfThisThread() =>
            Volatile.Write(ref _armedThread, Environment.CurrentManagedThreadId);

        /// <summary>Para o dispatcher que não lê a hora depois da chamada: o avanço vem com o lote já parado.</summary>
        public void ReachTheCutIfTheBatchIsParked()
        {
            if (Interlocked.Exchange(ref _armedThread, Disarmed) != Disarmed)
            {
                Reach("com o lote parado esperando a chamada");
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            var now = inner.GetUtcNow();
            var thread = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref _armedThread, Disarmed, thread) == thread)
            {
                Reach("logo depois de o dispatcher ler a hora, antes do passo seguinte");
            }

            return now;
        }

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);

        private void Reach(string moment)
        {
            CutReached = $"o relógio chegou ao corte {moment}";
            inner.Advance(CallTimeout);
        }
    }

    /// <summary>Início de sessão que nunca responde e ignora o cancelamento; arma o avanço do relógio na própria chamada.</summary>
    private sealed class StartThatIgnoresTheCut(CutWindowClock clock) : ISelfieProvider
    {
        private readonly TaskCompletionSource<SelfieSession> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCalls;

        public int StartCalls => Volatile.Read(ref _startCalls);

        public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCalls);
            clock.ReachTheCutAtTheNextReadOfThisThread();
            return _answer.Task;
        }

        public Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Este teste só abre sessão.");

        /// <summary>Encerra a chamada abandonada, para ela não sobreviver ao teste.</summary>
        public void Release() => _answer.TrySetCanceled(CancellationToken.None);
    }

    /// <summary>
    /// Roda o lote numa única thread, um passo por vez: cada continuação do dispatcher vira um passo da fila. Ao fim de
    /// cada passo o lote está parado num await, e é aí que o teste pode agir, sem corrida com ele.
    /// </summary>
    private sealed class SingleThreadPump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _steps = [];
        private readonly Lock _gate = new();
        private bool _closed;

        /// <summary>
        /// Depois do teste, o que o lote ainda publicar (a chamada abandonada que termina, por exemplo) roda no pool: uma
        /// continuação perdida num contexto descartado derrubaria o processo de testes.
        /// </summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _steps.Add((d, state));
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(callback => d(callback), state, preferLocal: false);
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("O lote não deveria pedir execução síncrona no contexto do teste.");

        public override SynchronizationContext CreateCopy() => this;

        public Task<T> Start<T>(Func<Task<T>> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return start();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void RunUntilCompleted(Task task, TimeSpan ceiling, Action afterEachStep, Func<string> state)
        {
            afterEachStep();
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                var left = ceiling - elapsed.Elapsed;
                if (left <= TimeSpan.Zero || !_steps.TryTake(out var step, (int)left.TotalMilliseconds, Ct))
                {
                    throw new TimeoutException(
                        $"O lote não encerrou em {ceiling.TotalSeconds:F0} s depois do corte ({state()}).");
                }

                Run(step.Callback, step.State);
                afterEachStep();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _steps.Dispose();
            }
        }

        private void Run(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
