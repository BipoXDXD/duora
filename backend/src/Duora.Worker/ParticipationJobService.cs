using System.Data.Common;
using Duora.Infrastructure;
using Duora.Infrastructure.Participation;

namespace Duora.Worker;

/// <summary>
/// Laço dos jobs de participação. A durabilidade está nas linhas de <c>ops.jobs</c>: uma falha registra a causa, tenta
/// devolver o job à fila e espera; se nem isso der certo, o lease vencido o devolve. Nenhuma falha termina o laço, porque
/// um <see cref="BackgroundService"/> que termina com exceção para o worker inteiro.
/// </summary>
internal sealed partial class ParticipationJobService(ParticipationService participation, ILogger<ParticipationJobService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await CompleteNextJobSafelyAsync(stoppingToken))
            {
                await Task.Delay(Pause, stoppingToken);
            }
        }
    }

    /// <returns><see langword="true"/> se concluiu um job e pode haver outro; senão, a fila está vazia ou algo falhou.</returns>
    private async Task<bool> CompleteNextJobSafelyAsync(CancellationToken stoppingToken)
    {
        ParticipationJob? job = null;
        try
        {
            job = await participation.ClaimJobAsync(stoppingToken);
            if (job is null)
            {
                return false;
            }

            await participation.CompleteJobAsync(job, stoppingToken);
            return true;
        }
        catch (Exception exception) when (IsFailure(exception, stoppingToken))
        {
            LogFailure(exception);
            if (job is not null)
            {
                await RetryJobSafelyAsync(job, stoppingToken);
            }

            return false;
        }
    }

    private async Task RetryJobSafelyAsync(ParticipationJob job, CancellationToken stoppingToken)
    {
        try
        {
            await participation.RetryJobAsync(job, stoppingToken);
        }
        catch (Exception exception) when (IsFailure(exception, stoppingToken))
        {
            LogFailure(exception); // Lease expiry remains the fallback if retry persistence fails.
        }
    }

    private static bool IsFailure(Exception exception, CancellationToken stoppingToken) =>
        exception is DbException || (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested);

    private void LogFailure(Exception exception)
    {
        if (exception is DbException)
        {
            LogDatabaseFailure(logger);
            return;
        }

        LogUnexpectedFailure(logger, exception.GetType().Name, ExceptionFrames.Describe(exception));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Participation job failed; the persisted lease returns it to the queue.")]
    private static partial void LogDatabaseFailure(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Participation job failed unexpectedly ({Cause}); the persisted lease returns it to the queue. Frames: {Frames}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string cause, string frames);
}
