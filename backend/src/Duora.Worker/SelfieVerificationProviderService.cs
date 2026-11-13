using System.Data.Common;
using Duora.Infrastructure;
using Duora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Worker;

internal sealed partial class SelfieVerificationProviderService(IServiceScopeFactory scopes, ILogger<SelfieVerificationProviderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is DbException
                or InvalidOperationException { InnerException: DbException }
                or DbUpdateException { InnerException: DbException })
            {
                DatabaseUnavailable(logger);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
            {
                LogUnexpectedFailure(logger, exception.GetType().Name, ExceptionFrames.Describe(exception));
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Selfie queue unavailable; retrying without logging payloads.")]
    private static partial void DatabaseUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Selfie dispatch failed unexpectedly ({Cause}); the delivery returns when its lease expires. Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
