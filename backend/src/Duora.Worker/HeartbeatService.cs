using Duora.Core.Jobs;
using Duora.Infrastructure;

namespace Duora.Worker;

/// <summary>
/// Verifica periodicamente se o worker alcança o banco: sucesso em Debug, falha em Warning com a causa.
/// Serve de modelo para os jobs: um escopo de DI por iteração (serviços scoped, como o DbContext, não atravessam
/// iterações), TimeProvider injetado e intervalo com jitter. A sonda não lança; o resto do passo (escopo de DI) pode, e o
/// erro inesperado vira log, para o heartbeat não parar o worker que ele existe para observar.
/// </summary>
internal sealed partial class HeartbeatService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly JitteredInterval Interval = new(TimeSpan.FromSeconds(30), jitterFraction: 0.1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportSafelyAsync(stoppingToken);
            await Task.Delay(Interval.NextDelay(Random.Shared.NextDouble()), timeProvider, stoppingToken);
        }
    }

    private async Task ReportSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReportAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            LogUnexpectedFailure(logger, exception.GetType().Name, ExceptionFrames.Describe(exception));
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<DatabaseProbe>();
        var status = await probe.CheckAsync(cancellationToken);

        if (status.Failure is null)
        {
            LogAlive(logger);
        }
        else
        {
            LogDatabaseUnavailable(logger, status.Failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker ativo; banco acessível")]
    private static partial void LogAlive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker ativo; banco inacessível")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat falhou de forma inesperada ({Cause}); tenta de novo no próximo intervalo. Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
