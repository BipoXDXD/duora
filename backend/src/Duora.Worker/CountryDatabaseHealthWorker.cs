using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Options;

namespace Duora.Worker;

internal sealed partial class CountryDatabaseHealthWorker(CountryDatabaseHealthService service,
    IOptions<CountryDatabaseRuntimeOptions> options, TimeProvider time, ILogger<CountryDatabaseHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            { await service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogFailure(logger); }
            await Task.Delay(options.Value.MonitorInterval, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database health invocation failed; updater is independent.")]
    private static partial void LogFailure(ILogger logger);
}
