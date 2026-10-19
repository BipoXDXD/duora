using System.Collections.Immutable;

namespace Duora.Core.Experiences.Constellation;

/// <summary>
/// Regras da Experiência B, Constelação de Escolhas (spec 6.3): as duas pessoas respondem ao mesmo cartão em
/// privado, escolhendo uma opção aprovada ou pulando. O cartão só é revelado quando as duas confirmam uma opção; um
/// "pular" ou o prazo encerram o cartão com saída neutra, sem guardar nem inventar resposta. Com as duas respostas
/// o próximo cartão abre na hora: o comentário é conversa da sala e não segura o jogo. O resumo final traz só o que
/// foi revelado, sem pontuação, diagnóstico nem promessa de compatibilidade.
/// </summary>
public sealed class ConstellationRules
    : IExperienceRules<ConstellationContent, ConstellationState, ConstellationAction, ConstellationView>
{
    public static readonly ExperienceId ExperienceId = new("choice-constellation");

    private const int RulesVersion = 1;
    private const int SchemaVersion = 1;

    public ExperienceDescriptor Descriptor { get; } = new(
        ExperienceId,
        RulesVersion,
        SchemaVersion,
        [ConstellationAction.ChooseOptionType, ConstellationAction.SkipCardType]);

    /// <summary>Não há sorteio: os dois recebem os mesmos cartões, na ordem do conteúdo. A semente fica sem uso.</summary>
    public ConstellationState CreateInitialState(
        ConstellationContent content, ParticipantPair participants, SessionSeed seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(participants);

        return new ConstellationState(
            ConstellationPublicState.Initial,
            NoAnswers(participants),
            new ConstellationControlState(content, participants));
    }

    public RejectionReason? Validate(ConstellationState state, ParticipantId actor, ConstellationAction action)
    {
        ArgumentNullException.ThrowIfNull(state);

        var card = CurrentCard(state);
        return action switch
        {
            ConstellationAction.ChooseOption choice => ValidateChoice(state, actor, card, choice),
            ConstellationAction.SkipCard skip => ValidateAnswer(state, actor, card, skip.CardId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Ação fora do conjunto fechado."),
        };
    }

    public RulesTransition<ConstellationState> Apply(
        ConstellationState state, ParticipantId actor, ConstellationAction action)
    {
        ArgumentNullException.ThrowIfNull(state);

        CardAnswer answer = action switch
        {
            ConstellationAction.ChooseOption choice => new CardAnswer.Chosen(
                CurrentCard(state).FindOption(choice.OptionId)
                ?? throw new InvalidOperationException("Escolha aplicada sem validação.")),
            ConstellationAction.SkipCard => new CardAnswer.Skipped(),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Ação fora do conjunto fechado."),
        };

        var answers = state.PrivateByParticipant.SetItem(actor, new ConstellationPrivateState(answer));
        if (answers.Values.Any(own => own.Answer is null))
        {
            return new RulesTransition<ConstellationState>(state.With(state.Shared, answers), IsConcluded: false);
        }

        return CloseCurrentCard(state, Resolve(CurrentCard(state), answers));
    }

    /// <summary>
    /// Timeout é saída neutra (spec 6.3): o cartão aberto passa sem revelação e a resposta que já existia é
    /// descartada. A sessão expira; o que foi revelado antes continua no resumo.
    /// </summary>
    public RulesTransition<ConstellationState> ResolveDeadline(ConstellationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var outcomes = state.Shared.Outcomes.Add(new CardOutcome.Passed(CurrentCard(state)));
        var shared = new ConstellationPublicState(ConstellationStage.Summary, outcomes);
        return new RulesTransition<ConstellationState>(
            state.With(shared, NoAnswers(state.Control.Participants)), IsConcluded: false);
    }

    public ConstellationView Project(
        GameSession<ConstellationState, ConstellationAction> session, ParticipantId viewer)
    {
        ArgumentNullException.ThrowIfNull(session);

        var state = session.State;
        var isAnswering = session.Status == SessionStatus.Active && state.Shared.Stage == ConstellationStage.Cards;
        // Sem filtrar por isAnswering: as regras apagam as respostas ao fechar o cartão e no prazo, e um estado que
        // as mantivesse por engano precisa aparecer (para a própria pessoa) em vez de ficar escondido aqui.
        var ownAnswer = state.PrivateByParticipant[viewer].Answer;
        var partner = state.Control.Participants.PartnerOf(viewer);

        return new ConstellationView(
            session.Experience.ExperienceId.Value,
            session.Experience.RulesVersion,
            session.Experience.ContentVersion,
            session.Experience.SchemaVersion,
            session.Version.Value,
            session.Status,
            session.Schedule.Deadline,
            state.Shared.Stage,
            state.Control.Content.Title,
            state.Control.Content.Objective,
            state.Control.Content.Cards.Count,
            isAnswering ? ToView(CurrentCard(state), state.Shared.Outcomes.Count) : null,
            ownAnswer is null ? null : ToView(ownAnswer),
            isAnswering && ownAnswer is not null,
            [.. state.Shared.Outcomes.OfType<CardOutcome.Revealed>().Select(revealed => ToView(revealed, viewer, partner))],
            [.. state.Shared.Outcomes.OfType<CardOutcome.Passed>().Select(passed => ToView(passed))],
            isAnswering && ownAnswer is null
                ? [ConstellationAction.ChooseOptionType.Value, ConstellationAction.SkipCardType.Value]
                : []);
    }

    public AccessibleInteraction DescribeAccessibleInteraction(string locale) => LocalizedCatalog.ForLocale(locale,
        new AccessibleInteraction(Descriptor,
            "Constellation of Choices: answer lighthearted cards together. Use text, buttons and keyboard; no dragging, color perception or reaction speed is required. Skipping is always allowed. The deadline is an absolute time.",
            [
                new StageInstruction("cards", "Choose an option and confirm, or skip. Your answer stays private until both people choose; then both choices appear together in text. If someone skips, neither answer is shown. Discussing a card in the room chat is optional."),
                new StageInstruction("summary", "The summary lists revealed cards and cards passed without revealing answers. There are no scores or diagnoses."),
            ],
            [
                new ActionInstruction(ConstellationAction.ChooseOptionType, "Use arrow keys to move between radio options, Space to select and Enter on Confirm.", [InputMethod.Button, InputMethod.Keyboard]),
                new ActionInstruction(ConstellationAction.SkipCardType, "Tab to Skip, then press Enter or Space. No answer is revealed and the summary does not identify who skipped.", [InputMethod.Button, InputMethod.Keyboard]),
            ]), DescribeAccessibleInteraction());

    public AccessibleInteraction DescribeAccessibleInteraction() => new(
        Descriptor,
        "Constelação de Escolhas: vocês respondem aos mesmos cartões leves e descobrem o que têm em comum. Tudo é "
        + "texto e botões; o prazo é um horário absoluto e nenhuma ação depende de arrastar, de cor ou de tempo de "
        + "reação. Pular é sempre permitido.",
        [
            new StageInstruction("cards",
                "Cada cartão traz uma pergunta e opções em lista. Escolha uma opção e ative \"Confirmar\", ou ative "
                + "\"Pular\". Sua resposta fica privada até as duas pessoas confirmarem uma opção; então as duas "
                + "escolhas aparecem juntas, em texto. Se alguém pular, o cartão passa sem mostrar nenhuma escolha. Comentar "
                + "o cartão no chat da sala é opcional."),
            new StageInstruction("summary",
                "O resumo lista em texto só os cartões revelados e os que passaram sem revelação. Não há nota, "
                + "pontuação nem diagnóstico."),
        ],
        [
            new ActionInstruction(ConstellationAction.ChooseOptionType,
                "Opções em botões de rádio: setas para mudar, Espaço para marcar e Enter em \"Confirmar\".",
                [InputMethod.Button, InputMethod.Keyboard]),
            new ActionInstruction(ConstellationAction.SkipCardType,
                "Botão \"Pular\": chegue com Tab e ative com Enter ou Espaço. Se alguém pular, o cartão passa sem mostrar "
                + "nenhuma escolha; o resumo não indica quem pulou.",
                [InputMethod.Button, InputMethod.Keyboard]),
        ]);

    private static RejectionReason? ValidateChoice(
        ConstellationState state, ParticipantId actor, ConstellationCard card, ConstellationAction.ChooseOption choice)
    {
        if (ValidateAnswer(state, actor, card, choice.CardId) is { } reason)
        {
            return reason;
        }

        return card.FindOption(choice.OptionId) is null ? RejectionReason.UnknownOption : null;
    }

    /// <summary>Regras comuns a escolher e pular: o cartão é o aberto e a pessoa ainda não respondeu a ele.</summary>
    private static RejectionReason? ValidateAnswer(
        ConstellationState state, ParticipantId actor, ConstellationCard card, string cardId)
    {
        if (card.CardId != cardId)
        {
            return RejectionReason.StaleStep;
        }

        return state.PrivateByParticipant[actor].Answer is null ? null : RejectionReason.AlreadyActed;
    }

    /// <summary>Dupla confirmação: revela só se as duas pessoas escolheram; qualquer "pular" deixa o cartão neutro.</summary>
    private static CardOutcome Resolve(
        ConstellationCard card, ImmutableDictionary<ParticipantId, ConstellationPrivateState> answers)
    {
        var choices = ImmutableDictionary.CreateBuilder<ParticipantId, ConstellationOption>();
        foreach (var (participant, own) in answers)
        {
            if (own.Answer is not CardAnswer.Chosen chosen)
            {
                return new CardOutcome.Passed(card);
            }

            choices.Add(participant, chosen.Option);
        }

        return new CardOutcome.Revealed(card, choices.ToImmutable());
    }

    /// <summary>Registra o resultado, apaga as respostas privadas e abre o próximo cartão ou o resumo.</summary>
    private static RulesTransition<ConstellationState> CloseCurrentCard(ConstellationState state, CardOutcome outcome)
    {
        var outcomes = state.Shared.Outcomes.Add(outcome);
        var isLastCard = outcomes.Count == state.Control.Content.Cards.Count;
        var shared = new ConstellationPublicState(
            isLastCard ? ConstellationStage.Summary : ConstellationStage.Cards, outcomes);
        return new RulesTransition<ConstellationState>(
            state.With(shared, NoAnswers(state.Control.Participants)), isLastCard);
    }

    private static ImmutableDictionary<ParticipantId, ConstellationPrivateState> NoAnswers(ParticipantPair participants) =>
        ImmutableDictionary<ParticipantId, ConstellationPrivateState>.Empty
            .Add(participants.First, ConstellationPrivateState.Unanswered)
            .Add(participants.Second, ConstellationPrivateState.Unanswered);

    /// <summary>
    /// Cartão aberto. Sessão ativa está sempre em <see cref="ConstellationStage.Cards"/>, e o motor só chama
    /// <c>Validate</c>, <c>Apply</c> e <c>ResolveDeadline</c> em sessão ativa; sem cartão aberto é estado impossível.
    /// </summary>
    private static ConstellationCard CurrentCard(ConstellationState state)
    {
        if (state.Shared.Stage != ConstellationStage.Cards)
        {
            throw new InvalidOperationException("Não há cartão aberto: a sessão já chegou ao resumo.");
        }

        return state.Control.Content.Cards[state.Shared.Outcomes.Count];
    }

    private static CardView ToView(ConstellationCard card, int index) => new(
        card.CardId,
        index + 1,
        card.Prompt,
        [.. card.Options.Select(ToView)]);

    private static CardOptionView ToView(ConstellationOption option) => new(option.OptionId, option.Label);

    private static OwnAnswerView ToView(CardAnswer answer) => answer switch
    {
        CardAnswer.Chosen chosen => new OwnAnswerView(OwnAnswerKind.Chosen, ToView(chosen.Option)),
        CardAnswer.Skipped => new OwnAnswerView(OwnAnswerKind.Skipped, Option: null),
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "Resposta fora do conjunto fechado."),
    };

    private static RevealedCardView ToView(CardOutcome.Revealed revealed, ParticipantId viewer, ParticipantId partner)
    {
        var mine = revealed.ChoiceByParticipant[viewer];
        var theirs = revealed.ChoiceByParticipant[partner];
        return new RevealedCardView(
            revealed.Card.CardId, revealed.Card.Prompt, mine.Label, theirs.Label, mine.OptionId == theirs.OptionId);
    }

    private static PassedCardView ToView(CardOutcome.Passed passed) => new(passed.Card.CardId, passed.Card.Prompt);
}
