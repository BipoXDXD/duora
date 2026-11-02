using System.Data.Common;
using Duora.Infrastructure.Identity;

namespace Duora.Worker;

internal sealed partial class ExternalAuthFlowRetentionService(ExternalAuthFlowRetention retention, TimeProvider time,
    ILogger<ExternalAuthFlowRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await retention.PurgeExpiredAsync(stoppingToken);
            }
            catch (DbException)
            {
                CleanupUnavailable(logger);
            }
            catch (TimeoutException)
            {
                CleanupUnavailable(logger);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "External authentication expiry cleanup unavailable; retrying without extending proof validity.")]
    private static partial void CleanupUnavailable(ILogger logger);
}
