using System.Data.Common;
using Duora.Core.Jobs;
using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>
/// Laço do consumidor <c>media-processing</c>: um upload por vez (concorrência 1, spec 4.3), um escopo de DI por
/// volta. Com o upload desligado (sem <c>Media:Review</c>), não consome nada. Banco fora do ar não derruba o worker.
/// </summary>
internal sealed partial class MediaProcessingService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MediaProcessingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan pollInterval;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<MediaProcessingDispatcher>();
            if (!dispatcher.Enabled)
            {
                LogDisabled(logger);
                return;
            }

            pollInterval = dispatcher.PollInterval;
        }

        var idle = new JitteredInterval(pollInterval, jitterFraction: 0.2);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await DispatchSafelyAsync(stoppingToken) == 0)
            {
                await Task.Delay(idle.NextDelay(Random.Shared.NextDouble()), timeProvider, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MediaProcessingDispatcher>().DispatchOnceAsync(stoppingToken);
        }
        catch (DbException exception)
        {
            LogDatabaseUnavailable(logger, exception);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload de fotos desligado (sem Media:Review); fila de mídia não é consumida")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fila de mídia não processada; banco inacessível")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);
}
