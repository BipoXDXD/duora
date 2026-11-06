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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(SelfieVerificationProviderService))]
    [InlineData(typeof(SelfieAttemptsSuspensionService))]
    [InlineData(typeof(TransactionalEmailService))]
    [InlineData(typeof(MediaProcessingService))]
    [InlineData(typeof(ExternalAuthFlowRetentionService))]
    public async Task AnUnexpectedErrorIsLoggedAndTheLoopKeepsRunning(Type loopType)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={postgres.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. MediaTestArguments,
            .. SelfieArguments,
        ]);
        builder.Services.AddSingleton<TimeProvider>(new ExplodingClock());
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();
        var loop = (BackgroundService)Assert.Single(host.Services.GetServices<IHostedService>(), service => service.GetType() == loopType);
        var error = logs.FirstWarningFrom(loopType.FullName!);

        await loop.StartAsync(Ct);
        await Task.WhenAny(error, loop.ExecuteTask!).WaitAsync(Ceiling, Ct);

        Assert.False(loop.ExecuteTask!.IsCompleted, $"O loop terminou com {loop.ExecuteTask.Exception?.InnerException?.GetType().Name}.");
        var entry = await error;
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(ExplodingClockException), entry.Text, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
        await loop.StopAsync(Ct);
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

    private sealed class ExplodingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new ExplodingClockException();
    }

    private sealed class ExplodingClockException : Exception;
}
