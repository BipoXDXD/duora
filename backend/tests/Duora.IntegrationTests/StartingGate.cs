using System.Globalization;

namespace Duora.IntegrationTests;

/// <summary>
/// Largada para testes de concorrência: libera todos os participantes juntos quando o último chega. Substitui o
/// <see cref="Barrier"/>, cuja espera síncrona prendia threads do pool enquanto o pool injetava as que faltavam (#459).
/// Aqui a espera é assíncrona e tem prazo: participante que não chega vira <see cref="TimeoutException"/> com a contagem,
/// em vez de travar o teste até o timeout do job.
/// </summary>
internal sealed class StartingGate
{
    /// <summary>Prazo dos testes de recepção: os clientes já existem antes da largada, então só estoura se um participante travar.</summary>
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(30);

    private readonly int _participants;
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public StartingGate(int participants)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(participants);
        _participants = participants;
    }

    public async Task ArriveAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var arrived = Interlocked.Increment(ref _arrived);
        if (arrived > _participants)
        {
            throw new InvalidOperationException(
                $"A largada espera {_participants} participantes, e chegou o {arrived}º.");
        }

        if (arrived == _participants)
        {
            _open.TrySetResult();
        }

        try
        {
            await _open.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                "Largada não abriu em {0} s: chegaram {1} de {2} participantes.",
                timeout.TotalSeconds,
                Math.Min(Volatile.Read(ref _arrived), _participants),
                _participants));
        }
    }
}
