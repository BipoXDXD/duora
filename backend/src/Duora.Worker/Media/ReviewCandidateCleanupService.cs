using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>Recover prewritten preparation and approved-candidate obligations, independently of admission.</summary>
internal sealed partial class ReviewCandidateCleanupService(
    ReviewCandidateCleanupDispatcher dispatcher, ApprovedCandidateCleanupDispatcher approved,
    ILogger<ReviewCandidateCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!dispatcher.Enabled && !approved.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await dispatcher.DispatchOnceAsync(stoppingToken);
                processed += await approved.DispatchOnceAsync(stoppingToken);
                if (processed != 0)
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
