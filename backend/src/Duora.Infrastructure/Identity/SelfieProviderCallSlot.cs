namespace Duora.Infrastructure.Identity;

/// <summary>
/// A única vaga de chamada ao fornecedor da selfie neste processo. Ela só volta quando a chamada termina de fato: a
/// chamada que o corte abandona e que ignora o cancelamento segura a vaga até acabar, para nunca existirem duas em
/// paralelo. Cada réplica do worker tem a sua.
/// </summary>
internal sealed class SelfieProviderCallSlot
{
    private readonly Lock _gate = new();
    private bool _held;
    private Task? _abandoned;
    private bool _refusalReported;

    /// <summary>
    /// Falso enquanto outra chamada ocupa a vaga. <paramref name="firstRefusal"/> é verdadeiro só na primeira recusa
    /// de cada chamada abandonada, para o aviso não se repetir a cada lote.
    /// </summary>
    public bool TryAcquire(out bool firstRefusal)
    {
        lock (_gate)
        {
            if (_abandoned is { IsCompleted: true })
            {
                Free();
            }

            firstRefusal = _held && !_refusalReported;
            if (_held)
            {
                _refusalReported = true;
                return false;
            }

            _held = true;
            return true;
        }
    }

    /// <summary>Devolve a vaga agora, ou só quando <paramref name="stillRunning"/> terminar.</summary>
    public void Release(Task? stillRunning)
    {
        if (stillRunning is null)
        {
            lock (_gate)
            {
                Free();
            }

            return;
        }

        lock (_gate)
        {
            _abandoned = stillRunning;
        }

        _ = stillRunning.ContinueWith(static (finished, state) => ((SelfieProviderCallSlot)state!).Finish(finished), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Finish(Task finished)
    {
        // Ninguém mais lê a tarefa abandonada: a leitura da exceção evita que ela suba como não observada.
        _ = finished.Exception;
        lock (_gate)
        {
            if (ReferenceEquals(_abandoned, finished))
            {
                Free();
            }
        }
    }

    private void Free()
    {
        _held = false;
        _abandoned = null;
        _refusalReported = false;
    }
}
