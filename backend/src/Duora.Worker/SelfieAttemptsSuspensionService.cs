using System.Data.Common;
using Duora.Infrastructure;
using Duora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Worker;

/// <summary>
/// Pedidos de suspensão da décima reprovação da selfie (issue #300, ADR-0043), em loop próprio: uma falha do
/// fornecedor da selfie não para este consumidor, e vice-versa. Roda mesmo com a selfie desligada.
/// </summary>
internal sealed partial class SelfieAttemptsSuspensionService(IServiceScopeFactory scopes, ILogger<SelfieAttemptsSuspensionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SelfieAttemptsSuspensionDispatcher>().DispatchBatchAsync(stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Selfie suspension queue unavailable; retrying without logging payloads.")]
    private static partial void DatabaseUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Selfie suspension dispatch failed unexpectedly ({Cause}); the delivery returns when its lease expires. Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
