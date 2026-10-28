using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>Physical deletion obligations (API-M03) survive admission being disabled; an absent storage provider performs no work.</summary>
internal sealed partial class PhotoDeletionCleanupService(
    PhotoDeletionCleanupDispatcher dispatcher, ILogger<PhotoDeletionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!dispatcher.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await dispatcher.DispatchOnceAsync(stoppingToken) != 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogUnavailable(logger, exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Physical photo deletion unavailable ({Cause}); persisted obligations remain for recovery")]
    private static partial void LogUnavailable(ILogger logger, string cause);
}
