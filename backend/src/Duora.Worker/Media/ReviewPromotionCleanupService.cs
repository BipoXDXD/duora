using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>
/// Removes abandoned private promotions (generation-owned review derivatives) independently of admission; an
/// absent storage provider performs no work. Until a promotion is removed, its photo's tombstone stays (ADR-0035).
/// </summary>
internal sealed partial class ReviewPromotionCleanupService(
    ReviewPromotionCleanupDispatcher dispatcher, ILogger<ReviewPromotionCleanupService> logger) : BackgroundService
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Private promotion cleanup unavailable ({Cause}); persisted obligations remain for recovery")]
    private static partial void LogUnavailable(ILogger logger, string cause);
}
