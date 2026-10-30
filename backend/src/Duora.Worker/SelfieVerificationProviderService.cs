using System.Data.Common;
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
                // Roda mesmo com a selfie desligada: um pedido de suspensão já gravado não pode ficar parado (issue #300).
                await scope.ServiceProvider.GetRequiredService<SelfieAttemptsSuspensionDispatcher>().DispatchBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is DbException
                or InvalidOperationException { InnerException: DbException }
                or DbUpdateException { InnerException: DbException })
            {
                DatabaseUnavailable(logger);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Selfie queue unavailable; retrying without logging payloads.")]
    private static partial void DatabaseUnavailable(ILogger logger);
}
