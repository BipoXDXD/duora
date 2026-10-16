using System.Data.Common;
using Duora.Core.Jobs;
using Duora.Infrastructure.Identity;

namespace Duora.Worker;

/// <summary>
/// Laço do consumidor que abre verificações etárias no fornecedor. A durabilidade está na outbox; este serviço só
/// repete <see cref="AgeVerificationDispatcher.DispatchBatchAsync"/>, como o de e-mail. Sem fornecedor configurado,
/// termina na hora.
/// </summary>
internal sealed partial class AgeVerificationProviderService(
    AgeVerificationDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<AgeVerificationProviderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!dispatcher.IsEnabled)
        {
            return;
        }

        var idle = new JitteredInterval(dispatcher.PollInterval, jitterFraction: 0.2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = await DispatchSafelyAsync(stoppingToken);
            if (claimed < dispatcher.BatchSize)
            {
                await Task.Delay(idle.NextDelay(Random.Shared.NextDouble()), timeProvider, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await dispatcher.DispatchBatchAsync(stoppingToken);
        }
        catch (DbException exception)
        {
            LogDatabaseUnavailable(logger, exception);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox da verificação etária não processada; banco inacessível")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);
}
