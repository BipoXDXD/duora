namespace Duora.Infrastructure.Media;

/// <summary>
/// Teto de bytes de derivados promovidos que a API segura em memória ao mesmo tempo. O buffer de uma leitura fica
/// preso até o cliente terminar de baixar, e um cliente lento pode levar minutos: sem teto, poucas dezenas de leituras
/// esgotam a memória do container. Sem espaço, a leitura falha na hora, sem fila; quem chama responde 503.
/// </summary>
internal sealed class OwnerMediaReadBudget
{
    /// <summary>Quatro variantes no teto de 16 MiB do CHECK de <c>review_promotion_variants</c>.</summary>
    internal const long DefaultCapacityBytes = 64L * 1024 * 1024;

    private readonly long _capacityBytes;
    private long _reservedBytes;

    internal OwnerMediaReadBudget(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
    }

    /// <summary>Nulo quando os bytes não cabem; a reserva volta ao orçamento uma única vez, no Dispose.</summary>
    internal IDisposable? TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var reserved = Interlocked.Read(ref _reservedBytes);
            if (bytes > _capacityBytes - reserved)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, reserved + bytes, reserved) == reserved)
            {
                return new Reservation(this, bytes);
            }
        }
    }

    private void Release(long bytes) => Interlocked.Add(ref _reservedBytes, -bytes);

    private sealed class Reservation(OwnerMediaReadBudget budget, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(bytes);
            }
        }
    }
}

/// <summary>
/// O orçamento de memória das leituras está cheio. É <see cref="IOException"/> para seguir o caminho de
/// indisponibilidade que a admissão de mídia já transforma em 503 com Retry-After.
/// </summary>
internal sealed class OwnerMediaReadBudgetExhaustedException()
    : IOException("O orçamento de memória das leituras de mídia está cheio.");
