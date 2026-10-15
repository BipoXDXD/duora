using System.Collections.Immutable;

namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Regras da Experiência A, Rotas Improváveis (spec 6.2): introdução, pistas, escolhas alternadas, ajuste
/// cooperativo com revisões limitadas e conclusão. O resultado é um roteiro compartilhado, sem nota, e não decide
/// conexão: a decisão é um fluxo à parte.
/// </summary>
public sealed class UnlikelyRoutesRules
    : IExperienceRules<UnlikelyRoutesContent, UnlikelyRoutesState, UnlikelyRoutesAction, UnlikelyRoutesView>
{
    public static readonly ExperienceId ExperienceId = new("unlikely-routes");

    private const int RulesVersion = 1;
    private const int SchemaVersion = 1;
    private const int PairSize = 2;

    private readonly UnlikelyRoutesSettings _settings;

    public UnlikelyRoutesRules(UnlikelyRoutesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public ExperienceDescriptor Descriptor { get; } = new(
        ExperienceId,
        RulesVersion,
        SchemaVersion,
        [
            UnlikelyRoutesAction.AcknowledgeType,
            UnlikelyRoutesAction.ChooseOptionType,
            UnlikelyRoutesAction.ConfirmRouteType,
            UnlikelyRoutesAction.RequestRevisionType,
        ]);

    /// <summary>A semente sorteia quem recebe cada conjunto de pistas e quem escolhe a primeira parada.</summary>
    public UnlikelyRoutesState CreateInitialState(
        UnlikelyRoutesContent content, ParticipantPair participants, SessionSeed seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(participants);

        var random = new Random(seed.Value);
        var firstSetHolder = random.Next(PairSize) == 0 ? participants.First : participants.Second;
        var secondSetHolder = participants.PartnerOf(firstSetHolder);
        var firstChooser = random.Next(PairSize) == 0 ? firstSetHolder : secondSetHolder;

        var privateByParticipant = ImmutableDictionary<ParticipantId, RoutesPrivateState>.Empty
            .Add(firstSetHolder, new RoutesPrivateState([.. content.ClueSets[0]]))
            .Add(secondSetHolder, new RoutesPrivateState([.. content.ClueSets[1]]));
        var control = new RoutesControlState(content, participants, firstChooser, _settings.MaxRevisions);
        return new UnlikelyRoutesState(RoutesPublicState.Initial, privateByParticipant, control);
    }

    public RejectionReason? Validate(UnlikelyRoutesState state, ParticipantId actor, UnlikelyRoutesAction action)
    {
        ArgumentNullException.ThrowIfNull(state);

        return action switch
        {
            UnlikelyRoutesAction.Acknowledge acknowledge => ValidateAcknowledgement(state.Shared, actor, acknowledge),
            UnlikelyRoutesAction.ChooseOption choice => ValidateChoice(state, actor, choice),
            UnlikelyRoutesAction.ConfirmRoute => ValidateConfirmation(state.Shared, actor),
            UnlikelyRoutesAction.RequestRevision => ValidateRevision(state),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Ação fora do conjunto fechado."),
        };
    }

    public RulesTransition<UnlikelyRoutesState> Apply(
        UnlikelyRoutesState state, ParticipantId actor, UnlikelyRoutesAction action)
    {
        ArgumentNullException.ThrowIfNull(state);

        return action switch
        {
            UnlikelyRoutesAction.Acknowledge => Continue(state, MarkDone(state, actor)),
            UnlikelyRoutesAction.ChooseOption choice => Continue(state, Choose(state, choice)),
            UnlikelyRoutesAction.ConfirmRoute => Confirm(state, actor),
            UnlikelyRoutesAction.RequestRevision => Continue(state, Revise(state.Shared)),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Ação fora do conjunto fechado."),
        };
    }

    /// <summary>
    /// Prazo vencido sempre expira a sessão, em qualquer etapa e mesmo com o roteiro completo no ajuste: pela
    /// spec 6.2, só as duas confirmações concluem. O que já foi escolhido fica no estado. Decisão provisória, a
    /// confirmar na issue #9.
    /// </summary>
    public RulesTransition<UnlikelyRoutesState> ResolveDeadline(UnlikelyRoutesState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new RulesTransition<UnlikelyRoutesState>(state, IsConcluded: false);
    }

    public UnlikelyRoutesView Project(
        GameSession<UnlikelyRoutesState, UnlikelyRoutesAction> session, ParticipantId viewer)
    {
        ArgumentNullException.ThrowIfNull(session);

        var state = session.State;
        var shared = state.Shared;
        var content = state.Control.Content;
        var isActive = session.Status == SessionStatus.Active;
        var currentStop = isActive && shared.Stage == RoutesStage.Choices ? CurrentStop(state) : null;
        var partner = state.Control.Participants.PartnerOf(viewer);

        return new UnlikelyRoutesView(
            session.Experience.ExperienceId.Value,
            session.Experience.RulesVersion,
            session.Experience.ContentVersion,
            session.Experience.SchemaVersion,
            session.Version.Value,
            session.Status,
            session.Schedule.Deadline,
            shared.Stage,
            content.Title,
            content.Objective,
            content.Example,
            OwnClues(state, viewer),
            currentStop is null ? null : ToView(currentStop),
            currentStop is not null && NextChooser(state) == viewer,
            [.. shared.ChosenOptions.Select((option, index) => ToChosenView(content.Stops[index], option))],
            state.Control.MaxRevisions - shared.RevisionsUsed,
            isActive && MustAct(state, partner) && !MustAct(state, viewer),
            isActive ? [.. AvailableActions(state, viewer).Select(type => type.Value)] : []);
    }

    public AccessibleInteraction DescribeAccessibleInteraction() => new(
        Descriptor,
        "Rotas Improváveis: vocês montam juntos um passeio por uma cidade inventada. Tudo é texto e botões; o prazo "
        + "é um horário absoluto e nenhuma ação depende de arrastar, de cor ou de tempo de reação.",
        [
            new StageInstruction("introduction",
                "Leia o objetivo e o exemplo e ative \"Entendi\". A etapa avança quando as duas pessoas confirmarem."),
            new StageInstruction("clues",
                "Suas pistas aparecem em lista, com o aviso \"Só você vê\". Ative \"Entendi\" para seguir às escolhas."),
            new StageInstruction("choices",
                "Vocês se alternam. Na sua vez, escolha uma opção da lista e ative \"Escolher\"; fora da sua vez, a "
                + "tela avisa em texto que a escolha é da outra pessoa."),
            new StageInstruction("adjustment",
                "Revise o roteiro e ative \"Confirmar roteiro\", ou \"Pedir revisão\" enquanto houver revisões. O "
                + "número de revisões restantes aparece em texto."),
            new StageInstruction("conclusion", "O roteiro final aparece em lista. Não há nota nem pontuação."),
        ],
        [
            new ActionInstruction(UnlikelyRoutesAction.AcknowledgeType,
                "Botão \"Entendi\": chegue com Tab e ative com Enter ou Espaço.", [InputMethod.Button, InputMethod.Keyboard]),
            new ActionInstruction(UnlikelyRoutesAction.ChooseOptionType,
                "Opções em botões de rádio: setas para mudar, Espaço para marcar e Enter em \"Escolher\".",
                [InputMethod.Button, InputMethod.Keyboard]),
            new ActionInstruction(UnlikelyRoutesAction.ConfirmRouteType,
                "Botão \"Confirmar roteiro\": chegue com Tab e ative com Enter ou Espaço.",
                [InputMethod.Button, InputMethod.Keyboard]),
            new ActionInstruction(UnlikelyRoutesAction.RequestRevisionType,
                "Botão \"Pedir revisão\": apaga o roteiro e reabre as escolhas. Chegue com Tab e ative com Enter ou Espaço.",
                [InputMethod.Button, InputMethod.Keyboard]),
        ]);

    private static RejectionReason? ValidateAcknowledgement(
        RoutesPublicState shared, ParticipantId actor, UnlikelyRoutesAction.Acknowledge acknowledge)
    {
        if (!IsAcknowledgementStage(shared.Stage) || acknowledge.Stage != shared.Stage)
        {
            return RejectionReason.WrongStage;
        }

        return shared.StageDoneBy.Contains(actor) ? RejectionReason.AlreadyActed : null;
    }

    private static RejectionReason? ValidateChoice(
        UnlikelyRoutesState state, ParticipantId actor, UnlikelyRoutesAction.ChooseOption choice)
    {
        if (state.Shared.Stage != RoutesStage.Choices)
        {
            return RejectionReason.WrongStage;
        }

        if (NextChooser(state) != actor)
        {
            return RejectionReason.NotYourTurn;
        }

        var stop = CurrentStop(state);
        if (stop.StepId != choice.StepId)
        {
            return RejectionReason.StaleStep;
        }

        return stop.FindOption(choice.OptionId) is null ? RejectionReason.UnknownOption : null;
    }

    private static RejectionReason? ValidateConfirmation(RoutesPublicState shared, ParticipantId actor)
    {
        if (shared.Stage != RoutesStage.Adjustment)
        {
            return RejectionReason.WrongStage;
        }

        return shared.StageDoneBy.Contains(actor) ? RejectionReason.AlreadyActed : null;
    }

    private static RejectionReason? ValidateRevision(UnlikelyRoutesState state)
    {
        if (state.Shared.Stage != RoutesStage.Adjustment)
        {
            return RejectionReason.WrongStage;
        }

        return HasRevisionLeft(state) ? null : RejectionReason.RevisionLimitReached;
    }

    private static RulesTransition<UnlikelyRoutesState> Continue(UnlikelyRoutesState state, RoutesPublicState shared) =>
        new(state.With(shared), IsConcluded: false);

    /// <summary>"Entendi" de um participante; com os dois, a etapa avança e a lista recomeça vazia.</summary>
    private static RoutesPublicState MarkDone(UnlikelyRoutesState state, ParticipantId actor)
    {
        var shared = state.Shared with { StageDoneBy = state.Shared.StageDoneBy.Add(actor) };
        if (shared.StageDoneBy.Count < PairSize)
        {
            return shared;
        }

        var nextStage = shared.Stage == RoutesStage.Introduction ? RoutesStage.Clues : RoutesStage.Choices;
        return shared with { Stage = nextStage, StageDoneBy = [] };
    }

    private static RoutesPublicState Choose(UnlikelyRoutesState state, UnlikelyRoutesAction.ChooseOption choice)
    {
        var option = CurrentStop(state).FindOption(choice.OptionId)
            ?? throw new InvalidOperationException("Escolha aplicada sem validação.");
        var chosen = state.Shared.ChosenOptions.Add(option);
        var stage = chosen.Count == state.Control.Content.Stops.Count ? RoutesStage.Adjustment : RoutesStage.Choices;
        return state.Shared with { ChosenOptions = chosen, Stage = stage };
    }

    private static RulesTransition<UnlikelyRoutesState> Confirm(UnlikelyRoutesState state, ParticipantId actor)
    {
        var shared = state.Shared with { StageDoneBy = state.Shared.StageDoneBy.Add(actor) };
        var bothConfirmed = shared.StageDoneBy.Count == PairSize;
        return new RulesTransition<UnlikelyRoutesState>(
            state.With(bothConfirmed ? Conclude(shared) : shared), bothConfirmed);
    }

    private static RoutesPublicState Revise(RoutesPublicState shared) => shared with
    {
        Stage = RoutesStage.Choices,
        ChosenOptions = [],
        StageDoneBy = [],
        RevisionsUsed = shared.RevisionsUsed + 1,
    };

    private static RoutesPublicState Conclude(RoutesPublicState shared) =>
        shared with { Stage = RoutesStage.Conclusion, StageDoneBy = [] };

    private static bool IsAcknowledgementStage(RoutesStage stage) =>
        stage is RoutesStage.Introduction or RoutesStage.Clues;

    private static bool HasRevisionLeft(UnlikelyRoutesState state) =>
        state.Shared.RevisionsUsed < state.Control.MaxRevisions;

    private static RouteStop CurrentStop(UnlikelyRoutesState state) =>
        state.Control.Content.Stops[state.Shared.ChosenOptions.Count];

    /// <summary>As paradas pares ficam com quem começa e as ímpares com a outra pessoa.</summary>
    private static ParticipantId NextChooser(UnlikelyRoutesState state)
    {
        var control = state.Control;
        return state.Shared.ChosenOptions.Count % PairSize == 0
            ? control.FirstChooser
            : control.Participants.PartnerOf(control.FirstChooser);
    }

    /// <summary>Se o participante precisa agir para a etapa avançar.</summary>
    private static bool MustAct(UnlikelyRoutesState state, ParticipantId participant) => state.Shared.Stage switch
    {
        RoutesStage.Introduction or RoutesStage.Clues or RoutesStage.Adjustment =>
            !state.Shared.StageDoneBy.Contains(participant),
        RoutesStage.Choices => NextChooser(state) == participant,
        RoutesStage.Conclusion => false,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state.Shared.Stage, "Etapa desconhecida."),
    };

    private static IEnumerable<ActionType> AvailableActions(UnlikelyRoutesState state, ParticipantId viewer)
    {
        var stage = state.Shared.Stage;
        if (IsAcknowledgementStage(stage) && MustAct(state, viewer))
        {
            yield return UnlikelyRoutesAction.AcknowledgeType;
        }

        if (stage == RoutesStage.Choices && MustAct(state, viewer))
        {
            yield return UnlikelyRoutesAction.ChooseOptionType;
        }

        if (stage == RoutesStage.Adjustment && MustAct(state, viewer))
        {
            yield return UnlikelyRoutesAction.ConfirmRouteType;
        }

        if (stage == RoutesStage.Adjustment && HasRevisionLeft(state))
        {
            yield return UnlikelyRoutesAction.RequestRevisionType;
        }
    }

    /// <summary>Só as pistas do próprio participante, e só depois da introdução.</summary>
    private static string[] OwnClues(UnlikelyRoutesState state, ParticipantId viewer) =>
        state.Shared.Stage == RoutesStage.Introduction
            ? []
            : [.. state.PrivateByParticipant[viewer].Clues.Select(clue => clue.Text)];

    private static RouteStopView ToView(RouteStop stop) => new(
        stop.StepId,
        stop.Prompt,
        [.. stop.Options.Select(option => new RouteOptionView(option.OptionId, option.Label))]);

    private static ChosenStopView ToChosenView(RouteStop stop, RouteOption option) =>
        new(stop.StepId, stop.Prompt, option.Label);
}
