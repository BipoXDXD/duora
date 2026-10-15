namespace Duora.Core.Experiences;

/// <summary>
/// Strategy com as regras de uma versão de experiência (spec 3.4, 24.9). A variação é real: Rotas Improváveis e
/// Constelação de Escolhas (spec 6.2 e 6.3) têm etapas, ações e projeções diferentes. O que é comum a todas
/// (participação, idempotência, versão e prazo) fica no <see cref="ExperienceEngine{TContent,TState,TAction,TView}"/>,
/// que chama a estratégia só depois dessas checagens.
/// </summary>
/// <remarks>Implementações são puras: sem I/O, sem relógio e com aleatoriedade só a partir da semente.</remarks>
public interface IExperienceRules<in TContent, TState, TAction, out TView>
    where TContent : IExperienceContent
    where TState : class
    where TAction : class, IGameAction
    where TView : class
{
    ExperienceDescriptor Descriptor { get; }

    TState CreateInitialState(TContent content, ParticipantPair participants, SessionSeed seed);

    /// <summary>Regras da etapa para uma ação de participante de sessão ativa; <c>null</c> quando é permitida.</summary>
    RejectionReason? Validate(TState state, ParticipantId actor, TAction action);

    /// <summary>Aplica uma ação que <see cref="Validate"/> aceitou sobre o mesmo estado.</summary>
    RulesTransition<TState> Apply(TState state, ParticipantId actor, TAction action);

    /// <summary>
    /// Estado final quando o prazo vence. <see cref="RulesTransition{TState}.IsConcluded"/> verdadeiro conclui a
    /// sessão; falso a expira.
    /// </summary>
    RulesTransition<TState> ResolveDeadline(TState state);

    /// <summary>DTO do participante: só o que ele pode ver, montado do zero (INV-05).</summary>
    TView Project(GameSession<TState, TAction> session, ParticipantId viewer);

    AccessibleInteraction DescribeAccessibleInteraction();
}
