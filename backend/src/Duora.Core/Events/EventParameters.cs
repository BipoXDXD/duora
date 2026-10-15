namespace Duora.Core.Events;

/// <summary>
/// Snapshot imutável do conjunto de parâmetros (spec 4.3) que o evento recebe ao nascer. Mudar a configuração global
/// depois não altera um evento já criado; a versão e o id do conjunto ficam para rastrear qual regra valeu.
/// </summary>
public sealed record EventParameters
{
    public EventParameters(
        Guid parameterSetId,
        int version,
        int maxParticipants,
        int roundCount,
        TimeSpan roundWindow,
        TimeSpan checkInLeadTime,
        TimeSpan roundStartTolerance)
    {
        if (parameterSetId == Guid.Empty)
        {
            throw new ArgumentException("O conjunto de parâmetros precisa de id.", nameof(parameterSetId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParticipants, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(roundCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roundWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInLeadTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(roundStartTolerance, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(roundStartTolerance, roundWindow);

        ParameterSetId = parameterSetId;
        Version = version;
        MaxParticipants = maxParticipants;
        RoundCount = roundCount;
        RoundWindow = roundWindow;
        CheckInLeadTime = checkInLeadTime;
        RoundStartTolerance = roundStartTolerance;
    }

    public Guid ParameterSetId { get; }

    public int Version { get; }

    /// <summary>Teto de participantes confirmados por evento; a capacidade de cada evento fica entre 1 e este valor.</summary>
    public int MaxParticipants { get; }

    public int RoundCount { get; }

    /// <summary>Duração de uma rodada: convite, jogo e intervalo.</summary>
    public TimeSpan RoundWindow { get; }

    /// <summary>Quanto antes do início o check-in abre.</summary>
    public TimeSpan CheckInLeadTime { get; }

    /// <summary>
    /// Atraso máximo, depois do horário da rodada, em que ela ainda pode começar ou ser retomada antes de rodar.
    /// Passado esse prazo, a rodada não cabe mais na janela e é cancelada (spec 5.2).
    /// </summary>
    public TimeSpan RoundStartTolerance { get; }
}
