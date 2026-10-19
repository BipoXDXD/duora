using Duora.Infrastructure.Participation;

namespace Duora.Worker;

internal sealed partial class ParticipationJobService(ParticipationService participation, ILogger<ParticipationJobService> logger)
    : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Participation job failed; persisted lease and bounded retries preserve recovery.")]
    private static partial void LogFailure(ILogger logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ParticipationJob? job = null;
            try
            {
                job = await participation.ClaimJobAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                await participation.CompleteJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // A claimed job is recoverable after its persisted lease expires.
            }
            catch (Exception)
            {
                LogFailure(logger);
                if (job is not null)
                {
                    await participation.RetryJobAsync(job, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
