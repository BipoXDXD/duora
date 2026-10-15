namespace Duora.Core.Experiences;

/// <summary>
/// Início e prazo absoluto da sessão. O jogo fica ativo no intervalo semiaberto [início, prazo): no instante do
/// prazo nenhuma ação é aceita e o prazo já pode ser resolvido.
/// </summary>
public sealed record GameSchedule
{
    private GameSchedule(DateTimeOffset startedAt, DateTimeOffset deadline)
    {
        StartedAt = startedAt;
        Deadline = deadline;
    }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset Deadline { get; }

    /// <summary>Prazo = o menor entre início do jogo + jogo máximo e início da rodada + teto da rodada.</summary>
    public static GameSchedule Plan(DateTimeOffset roundStartedAt, DateTimeOffset gameStartedAt, GameTimeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(gameStartedAt, roundStartedAt);

        var roundCap = roundStartedAt + limits.MaxAfterRoundStart;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(gameStartedAt, roundCap);

        var playCap = gameStartedAt + limits.MaxPlay;
        return new GameSchedule(gameStartedAt, playCap < roundCap ? playCap : roundCap);
    }

    public bool IsDue(DateTimeOffset now) => now >= Deadline;
}
