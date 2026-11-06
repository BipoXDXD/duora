using System.Data.Common;
using Duora.Core.Jobs;
using Duora.Infrastructure;
using Duora.Infrastructure.Email;

namespace Duora.Worker;

/// <summary>
/// Laço do consumidor de e-mail. A durabilidade está nas linhas da outbox, não aqui: este serviço só repete
/// <see cref="TransactionalEmailDispatcher.DispatchBatchAsync"/>, emendando lotes enquanto há trabalho e esperando
/// com jitter quando a fila esvazia. Banco fora do ar ou erro inesperado não derrubam o worker; a próxima volta tenta de novo.
/// </summary>
internal sealed partial class TransactionalEmailService(
    TransactionalEmailDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<TransactionalEmailService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        catch (Exception exception) when (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            LogUnexpectedFailure(logger, exception.GetType().Name, ExceptionFrames.Describe(exception));
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox de e-mail não processada; banco inacessível")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox de e-mail falhou de forma inesperada ({Cause}); a entrega volta quando o lease vencer. Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
