namespace Duora.Core.Experiences;

/// <summary>Limites de tempo do jogo (spec 4.3), recebidos como snapshot da configuração quando a sessão nasce.</summary>
public sealed record GameTimeLimits
{
    public static readonly GameTimeLimits Default = new(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(9));

    /// <param name="maxPlay">Duração máxima do jogo ativo.</param>
    /// <param name="maxAfterRoundStart">O prazo nunca passa do início da rodada mais este intervalo.</param>
    public GameTimeLimits(TimeSpan maxPlay, TimeSpan maxAfterRoundStart)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPlay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAfterRoundStart, TimeSpan.Zero);

        MaxPlay = maxPlay;
        MaxAfterRoundStart = maxAfterRoundStart;
    }

    public TimeSpan MaxPlay { get; }

    public TimeSpan MaxAfterRoundStart { get; }
}
