using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Options;

namespace Duora.Worker;

internal sealed partial class CountryDatabaseRetentionWorker(CountryDatabaseRetentionService service,
    CountryDatabaseOperationsStore status, IOptions<CountryDatabaseRuntimeOptions> options, TimeProvider time, ILogger<CountryDatabaseRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await service.RunOnceAsync(stoppingToken);
                if (outcome == CountryDatabaseRetentionOutcome.Completed)
                { await status.RecordBackupFailureAsync(false, stoppingToken); }
                else if (outcome is CountryDatabaseRetentionOutcome.Unavailable or CountryDatabaseRetentionOutcome.IntegrityHold)
                {
                    await status.RecordBackupFailureAsync(true, stoppingToken);
                    LogFailure(logger);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                LogFailure(logger);
                try
                { await status.RecordBackupFailureAsync(true, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { LogFailure(logger); }
            }
            await Task.Delay(options.Value.RetentionInterval, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database retention/archive invocation failed; maintenance requires attention.")]
    private static partial void LogFailure(ILogger logger);
}
