namespace Duora.Core.Matching;

/// <summary>
/// Participante que já passou pelos filtros individuais da rodada (conta ativa, requisito etário, inscrição, check-in,
/// sem sanção e sem outra sala ocupada) e concorre a um par. Esses filtros são de quem monta o snapshot.
/// </summary>
public sealed record Candidate
{
    public Candidate(Guid userId, DateTimeOffset waitingSince, int roundsWithoutPair, Preferences preferences,
        CommunicationDeclaration? communication = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Candidato precisa de uma conta identificada.", nameof(userId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(roundsWithoutPair);
        ArgumentNullException.ThrowIfNull(preferences);

        UserId = userId;
        WaitingSince = waitingSince;
        RoundsWithoutPair = roundsWithoutPair;
        Preferences = preferences;
        Communication = communication;
    }

    public Guid UserId { get; }

    /// <summary>Instante desde o qual a pessoa espera por um par; quanto mais antigo, maior a prioridade.</summary>
    public DateTimeOffset WaitingSince { get; }

    /// <summary>Rodadas deste evento em que a pessoa ficou sem par; pesa antes do tempo de espera.</summary>
    public int RoundsWithoutPair { get; }

    public Preferences Preferences { get; }

    public CommunicationDeclaration? Communication { get; }
}
