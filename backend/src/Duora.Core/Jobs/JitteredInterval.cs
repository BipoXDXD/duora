namespace Duora.Core.Jobs;

/// <summary>
/// Intervalo de repetição de um job com jitter simétrico em torno do período, para que instâncias e jobs
/// iniciados juntos não sincronizem picos de carga no banco.
/// </summary>
public sealed record JitteredInterval
{
    public JitteredInterval(TimeSpan period, double jitterFraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        if (!IsInHalfOpenUnitInterval(jitterFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), jitterFraction, "Deve estar em [0, 1).");
        }

        Period = period;
        JitterFraction = jitterFraction;
    }

    public TimeSpan Period { get; }

    /// <summary>Desvio máximo, como fração do período, para cada lado (0,1 = ±10%).</summary>
    public double JitterFraction { get; }

    /// <summary>
    /// Devolve o próximo atraso a partir de uma amostra uniforme em [0, 1); 0,5 devolve exatamente o período.
    /// A amostra vem de fora para o cálculo ser determinístico em teste.
    /// </summary>
    public TimeSpan NextDelay(double sample)
    {
        if (!IsInHalfOpenUnitInterval(sample))
        {
            throw new ArgumentOutOfRangeException(nameof(sample), sample, "Deve estar em [0, 1).");
        }

        var offset = ((sample * 2) - 1) * JitterFraction;
        return Period * (1 + offset);
    }

    private static bool IsInHalfOpenUnitInterval(double value) => value is >= 0 and < 1;
}
