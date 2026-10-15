using System.Collections.Immutable;

namespace Duora.Core.Experiences.Constellation;

/// <summary>
/// Estado de uma sessão de Constelação de Escolhas em três estruturas separadas (spec 6.1): público (visto pelos
/// dois, só com o que já foi revelado), privado por participante (a resposta ao cartão atual) e controle (conteúdo
/// fixado e o par). Os membros são internos: o participante só recebe <see cref="ConstellationView"/>.
/// </summary>
public sealed class ConstellationState
{
    internal ConstellationState(
        ConstellationPublicState shared,
        ImmutableDictionary<ParticipantId, ConstellationPrivateState> privateByParticipant,
        ConstellationControlState control)
    {
        Shared = shared;
        PrivateByParticipant = privateByParticipant;
        Control = control;
    }

    internal ConstellationPublicState Shared { get; }

    internal ImmutableDictionary<ParticipantId, ConstellationPrivateState> PrivateByParticipant { get; }

    internal ConstellationControlState Control { get; }

    internal ConstellationState With(
        ConstellationPublicState shared,
        ImmutableDictionary<ParticipantId, ConstellationPrivateState> privateByParticipant) =>
        new(shared, privateByParticipant, Control);
}

/// <param name="Stage">Etapa atual.</param>
/// <param name="Outcomes">
/// Resultado de cada cartão já encerrado, na ordem dos cartões. O próximo cartão é o de índice
/// <c>Outcomes.Count</c>.
/// </param>
internal sealed record ConstellationPublicState(ConstellationStage Stage, ImmutableList<CardOutcome> Outcomes)
{
    public static readonly ConstellationPublicState Initial = new(ConstellationStage.Cards, []);
}

/// <param name="Answer">Resposta ao cartão atual; <c>null</c> enquanto a pessoa não respondeu.</param>
internal sealed record ConstellationPrivateState(CardAnswer? Answer)
{
    public static readonly ConstellationPrivateState Unanswered = new(Answer: null);
}

/// <param name="Content">Conteúdo fixado na criação (INV-09).</param>
/// <param name="Participants">O par da sessão, para montar a revelação de cada lado.</param>
internal sealed record ConstellationControlState(ConstellationContent Content, ParticipantPair Participants);

/// <summary>Resposta privada a um cartão. Conjunto fechado.</summary>
internal abstract record CardAnswer
{
    private CardAnswer()
    {
    }

    public sealed record Chosen(ConstellationOption Option) : CardAnswer;

    public sealed record Skipped : CardAnswer;
}

/// <summary>Como um cartão terminou. Conjunto fechado.</summary>
internal abstract record CardOutcome(ConstellationCard Card)
{
    /// <summary>As duas pessoas confirmaram uma opção: as duas escolhas passam a ser públicas no par.</summary>
    public sealed record Revealed(
        ConstellationCard Card, ImmutableDictionary<ParticipantId, ConstellationOption> ChoiceByParticipant)
        : CardOutcome(Card);

    /// <summary>
    /// Saída neutra: alguém pulou ou o prazo venceu. Não guarda nenhuma resposta nem quem pulou, para que nada
    /// possa vazar depois.
    /// </summary>
    public sealed record Passed(ConstellationCard Card) : CardOutcome(Card);
}
