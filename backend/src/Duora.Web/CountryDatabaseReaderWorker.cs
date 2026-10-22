using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Options;

namespace Duora.Web;

/// <summary>Refresh committed generations outside requests; every lookup still enforces expiry locally.</summary>
internal sealed partial class CountryDatabaseReaderWorker(ActiveCountryDatabase database, CountryDatabaseUpdateStore store,
    IOptions<CountryDatabaseRuntimeOptions> options, TimeProvider time, ILogger<CountryDatabaseReaderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            { await database.RefreshAsync(store, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogFailure(logger); }
            await Task.Delay(options.Value.RefreshInterval, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database refresh failed; suggestion remains subject to local expiry.")]
    private static partial void LogFailure(ILogger logger);
}
