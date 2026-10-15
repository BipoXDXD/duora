namespace Duora.Core.Events;

/// <summary>Horários do evento, derivados do início e do snapshot de parâmetros; nada aqui é guardado em dobro.</summary>
public sealed record EventSchedule
{
    public EventSchedule(DateTimeOffset startsAt, EventParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        StartsAt = startsAt;
        Parameters = parameters;
    }

    public DateTimeOffset StartsAt { get; }

    public EventParameters Parameters { get; }

    public DateTimeOffset CheckInOpensAt => StartsAt - Parameters.CheckInLeadTime;

    public DateTimeOffset EndsAt => StartsAt + (Parameters.RoundWindow * Parameters.RoundCount);

    /// <summary>Início da rodada <paramref name="roundNumber"/>, contada a partir de 1.</summary>
    public DateTimeOffset RoundStartsAt(int roundNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roundNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roundNumber, Parameters.RoundCount);

        return StartsAt + (Parameters.RoundWindow * (roundNumber - 1));
    }
}
