namespace Duora.Core.Experiences;

/// <summary>Conta que participa de uma sessão de jogo.</summary>
public readonly record struct ParticipantId
{
    public ParticipantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Não pode ser o GUID vazio.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}
