namespace Duora.Core.Jobs;

/// <summary>
/// Espera entre tentativas de um efeito que falhou: dobra a cada tentativa até o teto e sorteia na metade de cima
/// do intervalo, para que entregas que falharam juntas não voltem juntas.
/// </summary>
public sealed record ExponentialBackoff
{
    public ExponentialBackoff(TimeSpan initialDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, initialDelay);

        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Espera depois da tentativa <paramref name="attempt"/> (1 = primeira), a partir de uma amostra uniforme em
    /// [0, 1) recebida de fora para o cálculo ser determinístico em teste.
    /// </summary>
    public TimeSpan DelayAfter(int attempt, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (sample is not (>= 0 and < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(sample), sample, "Deve estar em [0, 1).");
        }

        // Em double para não estourar TimeSpan com expoente grande; o teto corta antes de virar infinito.
        var uncappedTicks = InitialDelay.Ticks * Math.Pow(2, attempt - 1);
        var baseTicks = Math.Min(uncappedTicks, MaxDelay.Ticks);
        var jitterFactor = 0.5 + (sample / 2);
        return TimeSpan.FromTicks((long)Math.Round(baseTicks * jitterFactor));
    }
}
