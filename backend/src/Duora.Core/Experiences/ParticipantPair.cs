namespace Duora.Core.Experiences;

/// <summary>As duas pessoas de uma sessão de jogo, distintas. A ordem não tem significado de regra.</summary>
public sealed record ParticipantPair
{
    public ParticipantPair(ParticipantId first, ParticipantId second)
    {
        if (first == second)
        {
            throw new ArgumentException("Um par precisa de duas pessoas diferentes.", nameof(second));
        }

        First = first;
        Second = second;
    }

    public ParticipantId First { get; }

    public ParticipantId Second { get; }

    public bool Contains(ParticipantId participant) => participant == First || participant == Second;

    public ParticipantId PartnerOf(ParticipantId participant)
    {
        if (!Contains(participant))
        {
            throw new ArgumentException("Não pertence ao par.", nameof(participant));
        }

        return participant == First ? Second : First;
    }
}
