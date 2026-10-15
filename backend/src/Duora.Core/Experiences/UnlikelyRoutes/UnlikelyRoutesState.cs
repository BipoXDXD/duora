using System.Collections.Immutable;

namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Estado de uma sessão de Rotas Improváveis em três estruturas separadas (spec 6.1): público (visto pelos dois),
/// privado por participante (pistas) e controle (conteúdo fixado, sorteio e limites). Os membros são internos: o
/// participante só recebe <see cref="UnlikelyRoutesView"/>.
/// </summary>
public sealed class UnlikelyRoutesState
{
    internal UnlikelyRoutesState(
        RoutesPublicState shared,
        ImmutableDictionary<ParticipantId, RoutesPrivateState> privateByParticipant,
        RoutesControlState control)
    {
        Shared = shared;
        PrivateByParticipant = privateByParticipant;
        Control = control;
    }

    internal RoutesPublicState Shared { get; }

    internal ImmutableDictionary<ParticipantId, RoutesPrivateState> PrivateByParticipant { get; }

    internal RoutesControlState Control { get; }

    internal UnlikelyRoutesState With(RoutesPublicState shared) => new(shared, PrivateByParticipant, Control);
}

/// <param name="Stage">Etapa atual.</param>
/// <param name="ChosenOptions">Opções escolhidas, uma por parada, na ordem das paradas.</param>
/// <param name="StageDoneBy">Quem já deu "entendi" ou confirmou o roteiro na etapa atual.</param>
/// <param name="RevisionsUsed">Revisões já consumidas na sessão.</param>
internal sealed record RoutesPublicState(
    RoutesStage Stage,
    ImmutableList<RouteOption> ChosenOptions,
    ImmutableHashSet<ParticipantId> StageDoneBy,
    int RevisionsUsed)
{
    public static readonly RoutesPublicState Initial = new(RoutesStage.Introduction, [], [], 0);
}

internal sealed record RoutesPrivateState(ImmutableList<Clue> Clues);

/// <param name="Content">Conteúdo fixado na criação (INV-09).</param>
/// <param name="Participants">O par da sessão, para calcular a vez de cada um.</param>
/// <param name="FirstChooser">Quem escolhe a primeira parada; depois as escolhas alternam.</param>
/// <param name="MaxRevisions">Snapshot de <see cref="UnlikelyRoutesSettings.MaxRevisions"/>.</param>
internal sealed record RoutesControlState(
    UnlikelyRoutesContent Content,
    ParticipantPair Participants,
    ParticipantId FirstChooser,
    int MaxRevisions);
