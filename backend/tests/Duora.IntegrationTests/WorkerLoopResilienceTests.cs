extern alias worker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using worker::Duora.Worker;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests;

/// <summary>
/// Um <see cref="BackgroundService"/> que termina com exceção para o host inteiro. Cada loop de fila precisa registrar
/// o erro inesperado e seguir, com a durabilidade na outbox. O relógio que explode simula um erro de programação fora
/// da lista de falhas conhecidas: cada dispatcher o lê antes de qualquer outra coisa.
/// </summary>
public sealed class WorkerLoopResilienceTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private const string MessageCanary = "CANARY-mensagem-com-dado";

    private const string InnerCanary = "CANARY-causa-com-dado";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(SelfieVerificationProviderService))]
    [InlineData(typeof(SelfieAttemptsSuspensionService))]
    [InlineData(typeof(TransactionalEmailService))]
    [InlineData(typeof(MediaProcessingService))]
    [InlineData(typeof(ExternalAuthFlowRetentionService))]
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
        var entry = await error.WaitAsync(Ceiling, Ct);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(ExplodingClockException), entry.Text, StringComparison.Ordinal);
        Assert.Contains($"{nameof(ExplodingClock)}.{nameof(ExplodingClock.GetUtcNow)}:line ", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Duora.", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageCanary, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(InnerCanary, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", entry.Text, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
        await loop.StopAsync(Ct);
    }

    /// <summary>O cancelamento que chega da parada do worker é rotina: sai do loop sem virar erro.</summary>
    [Theory]
    [InlineData(typeof(SelfieVerificationProviderService))]
    [InlineData(typeof(SelfieAttemptsSuspensionService))]
    [InlineData(typeof(TransactionalEmailService))]
    [InlineData(typeof(MediaProcessingService))]
    [InlineData(typeof(ExternalAuthFlowRetentionService))]
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

    private static BackgroundService LoopOf(IHost host, Type loopType) =>
        (BackgroundService)Assert.Single(host.Services.GetServices<IHostedService>(), service => service.GetType() == loopType);

    private IHost BuildWorker(ExplodingClock clock, CapturingLoggerProvider logs)
    {
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={postgres.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. MediaTestArguments,
            .. SelfieArguments,
        ]);
        builder.Services.AddSingleton<TimeProvider>(clock);
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

    /// <summary>
    /// Cada volta dos loops lê o relógio uma vez, e essa leitura explode: a segunda leitura prova a segunda volta. Os
    /// timers encurtados poupam a espera entre voltas, que na limpeza OIDC é de 1 minuto.
    /// </summary>
    private sealed class ExplodingClock : TimeProvider
    {
        private readonly TaskCompletionSource _firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public Task FirstPass => _firstPass.Task;

        public Task SecondPass => _secondPass.Task;

        public Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var reads = Interlocked.Increment(ref _reads);
            _firstPass.TrySetResult();
            if (reads >= 2)
            {
                _secondPass.TrySetResult();
            }

            OnRead?.Invoke();
            throw new ExplodingClockException();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.FromMilliseconds(1), period);
    }

    private sealed class ExplodingClockException() : Exception(MessageCanary, new InvalidOperationException(InnerCanary));
}
