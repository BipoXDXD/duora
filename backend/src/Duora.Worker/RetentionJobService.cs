using Duora.Infrastructure.Operations;

namespace Duora.Worker;

internal sealed partial class RetentionJobService(
    RetentionService retention, IConfiguration configuration, ILogger<RetentionJobService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention requires review: {UnreviewedFailures} old unreviewed deliveries and {FailedJobs} failed retention jobs.")]
    private static partial void LogReview(ILogger logger, long unreviewedFailures, long failedJobs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention operation failed; persisted leases and bounded retries preserve recovery.")]
    private static partial void LogFailure(ILogger logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Explicit rollout gate: schema and compatible readers/consumers must precede actual deletion.
        if (!configuration.GetValue<bool>("Retention:Enabled"))
        {
            return;
        }
        var observeAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            RetentionJob? job = null;
            try
            {
                if (DateTimeOffset.UtcNow >= observeAt)
                {
                    // Back off even when observation fails. Monitoring must never gate backlog recovery.
                    observeAt = DateTimeOffset.UtcNow.AddMinutes(1);
                    try
                    {
                        var health = await retention.ObserveAsync(stoppingToken);
                        if (health.UnreviewedFailures > 0 || health.FailedJobs > 0)
                        {
                            LogReview(logger, health.UnreviewedFailures, health.FailedJobs);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        LogFailure(logger);
                    }
                }
                job = await retention.ClaimAsync(stoppingToken);
                if (job is not null)
                {
                    await retention.ExecuteAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogFailure(logger); // Never log provider exceptions, SQL, identifiers or connection strings.
                if (job is not null)
                {
                    try
                    {
                        await retention.RetryAsync(job, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        LogFailure(logger); // Lease expiry remains the fallback if retry persistence fails.
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
