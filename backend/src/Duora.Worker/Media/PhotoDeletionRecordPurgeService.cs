using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>
/// Retenção de 1 ano dos tombstones e recibos de exclusão de foto (P3 da #173). Roda em toda instância do Worker: a
/// função do banco usa <c>SKIP LOCKED</c>, então instâncias juntas apagam linhas disjuntas e repetir é inócuo.
/// Enquanto um lote apaga algo, o próximo vem logo; sem nada elegível, espera cerca de uma hora, com jitter para as
/// instâncias não se alinharem.
/// </summary>
internal sealed partial class PhotoDeletionRecordPurgeService(
    PhotoDeletionRecordPurge purge, TimeProvider time, ILogger<PhotoDeletionRecordPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BacklogPause = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Interval + MaxJitter * Random.Shared.NextDouble();
            try
            {
                if ((await purge.PurgeOnceAsync(stoppingToken)).Total > 0)
                {
                    delay = BacklogPause;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Só o tipo: a mensagem do provedor pode trazer SQL ou a connection string.
                LogUnavailable(logger, exception.GetType().Name);
            }

            await Task.Delay(delay, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Photo deletion record purge unavailable ({Cause}); the records stay until the next attempt")]
    private static partial void LogUnavailable(ILogger logger, string cause);
}
