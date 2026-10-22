using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>Recover only prewritten preparation obligations, independently of admission being enabled.</summary>
internal sealed partial class ReviewCandidateCleanupService(
    ReviewCandidateCleanupDispatcher dispatcher, ILogger<ReviewCandidateCleanupService> logger) : BackgroundService
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Private candidate cleanup unavailable ({Cause}); persisted obligations remain for recovery")]
    private static partial void LogUnavailable(ILogger logger, string cause);
}
