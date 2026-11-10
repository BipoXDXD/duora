namespace Duora.Infrastructure.Media;

/// <summary>
/// Teto de bytes de derivados promovidos que a API segura em memória ao mesmo tempo. O buffer de uma leitura fica
/// preso até o cliente terminar de baixar, e um cliente lento pode levar minutos: sem teto, poucas dezenas de leituras
/// esgotam a memória do container. Para uma conta não prender o teto de todas, cada conta tem no máximo
/// <see cref="MaxReadsPerAccount"/> reservas abertas, e cada reserva vence em <see cref="ReservationLifetime"/>. Sem
/// espaço, a leitura falha na hora, sem fila; quem chama responde 503.
/// </summary>
internal sealed class OwnerMediaReadBudget
{
    /// <summary>Quatro variantes no teto de 16 MiB do CHECK de <c>review_promotion_variants</c>.</summary>
    internal const long DefaultCapacityBytes = 64L * 1024 * 1024;

    /// <summary>A tela do dono pede uma variante por vez; duas cobrem uma troca de tamanho em andamento.</summary>
    internal const int MaxReadsPerAccount = 2;

    /// <summary>
    /// Prazo absoluto da cópia: 16 MiB pedem ~280 KB/s, e um <c>full</c> típico, abaixo de 1 MiB, ~17 KB/s. Um
    /// cliente mais lento que isso perde a resposta, não o orçamento dos outros.
    /// </summary>
    internal static readonly TimeSpan ReservationLifetime = TimeSpan.FromSeconds(60);

    private readonly long _capacityBytes;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _readsByAccount = [];
    private long _reservedBytes;

    internal OwnerMediaReadBudget(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
    }

    /// <summary>Nulo quando os bytes não cabem ou a conta já tem o máximo aberto; a reserva volta uma única vez.</summary>
    internal IDisposable? TryReserve(Guid accountId, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            _readsByAccount.TryGetValue(accountId, out var reads);
            if (reads >= MaxReadsPerAccount || bytes > _capacityBytes - _reservedBytes)
            {
                return null;
            }

            _readsByAccount[accountId] = reads + 1;
            _reservedBytes += bytes;
        }

        return new Reservation(this, accountId, bytes);
    }

    private void Release(Guid accountId, long bytes)
    {
        lock (_gate)
        {
            _reservedBytes -= bytes;
            var reads = _readsByAccount[accountId] - 1;
            if (reads == 0)
            {
                _readsByAccount.Remove(accountId);
            }
            else
            {
                _readsByAccount[accountId] = reads;
            }
        }
    }

    private sealed class Reservation(OwnerMediaReadBudget budget, Guid accountId, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(accountId, bytes);
            }
        }
    }
}

/// <summary>
/// O orçamento de memória das leituras está cheio, ou a conta já tem o máximo de leituras abertas. É
/// <see cref="IOException"/> para seguir o caminho de indisponibilidade que a admissão de mídia já transforma em 503
/// com Retry-After.
/// </summary>
internal sealed class OwnerMediaReadBudgetExhaustedException()
    : IOException("O orçamento de memória das leituras de mídia está cheio.");
