using System.Data.Common;
using Duora.Core.Jobs;
using Duora.Infrastructure;
using Duora.Infrastructure.Email;

namespace Duora.Worker;

/// <summary>
/// Laço do consumidor dos fatos de promoção (ADR-0056). Desligado, registra isso uma vez e termina sem tocar no banco.
/// Ligado, repete <see cref="WaitlistPromotionNoticeScheduler.ScheduleBatchAsync"/>; falha de banco ou inesperada não
/// derruba o worker, e o fato sem marca volta na próxima volta.
/// </summary>
internal sealed partial class WaitlistPromotionNoticeService(
    WaitlistPromotionNoticeScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WaitlistPromotionNoticeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!scheduler.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var idle = new JitteredInterval(scheduler.PollInterval, jitterFraction: 0.2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ScheduleSafelyAsync(stoppingToken);
            if (processed < scheduler.BatchSize)
            {
                await Task.Delay(idle.NextDelay(Random.Shared.NextDouble()), timeProvider, stoppingToken);
            }
        }
    }

    private async Task<int> ScheduleSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await scheduler.ScheduleBatchAsync(stoppingToken);
        }
        catch (DbException exception)
        {
            // Só o tipo: a mensagem do banco pode citar valores. Inclui o lock_timeout de um fato disputado.
            LogDatabaseFailure(logger, exception.GetType().Name);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            LogUnexpectedFailure(logger, exception.GetType().Name, ExceptionFrames.Describe(exception));
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Avisos de promoção da lista de espera desligados")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fatos de promoção não processados ({Cause}); a próxima volta tenta de novo")]
    private static partial void LogDatabaseFailure(ILogger logger, string cause);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fatos de promoção falharam de forma inesperada ({Cause}). Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
