using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duora.Worker;

// WorkerHost registers this only through the explicit, validated runtime configuration.
internal sealed partial class CountryDatabaseUpdateWorker(
    CountryDatabaseUpdateStore store, CountryDatabaseUpdateService service, TimeProvider time, TimeSpan leaseDuration,
    bool enabled = false, ILogger<CountryDatabaseUpdateWorker>? logger = null) : BackgroundService
{
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (!enabled)
        { return false; }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var lease = await store.ClaimAsync(ct);
        if (lease is null)
        { return false; }
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = Renew(lease, ownership, stopRenewal.Token);
        try
        { await service.ExecuteAsync(lease, ownership.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Lost ownership: never publish. */ }
        finally { await stopRenewal.CancelAsync(); await renew; }
        return true;
    }

    private async Task Renew(CountryDatabaseUpdateLease lease, CancellationTokenSource ownership, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(leaseDuration / 3, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!await store.RenewAsync(lease, ct))
                { await ownership.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            LogFailure(logger ?? NullLogger<CountryDatabaseUpdateWorker>.Instance);
            await ownership.CancelAsync();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogFailure(logger ?? NullLogger<CountryDatabaseUpdateWorker>.Instance); }
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database update failed; persisted ownership must be revalidated before recovery.")]
    private static partial void LogFailure(ILogger logger);
}
