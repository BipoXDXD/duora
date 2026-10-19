namespace Duora.Core.Experiences.Constellation;

/// <summary>One published content version with two complete language variants. Legacy single-language content remains readable.</summary>
public sealed class LocalizedConstellationContent : IExperienceContent
{
    private readonly ConstellationContent _enUS;
    private readonly ConstellationContent _ptBR;

    public LocalizedConstellationContent(ConstellationContent enUS, ConstellationContent ptBR,
        IEnumerable<LocalizedAudio>? audio = null)
    {
        ArgumentNullException.ThrowIfNull(enUS);
        ArgumentNullException.ThrowIfNull(ptBR);
        if (enUS.ContentVersion != ptBR.ContentVersion)
        {
            throw new ArgumentException("Content versions must match.");
        }

        LocalizedCatalog.RequireStructure(Structure(enUS), Structure(ptBR));
        _enUS = enUS;
        _ptBR = ptBR;
        Audio = LocalizedCatalog.RequireAudio(audio);
    }

    public ExperienceId ExperienceId => _enUS.ExperienceId;
    public int ContentVersion => _enUS.ContentVersion;
    public IReadOnlyList<LocalizedAudio> Audio { get; }

    public IReadOnlyList<LocalizedAudioView> ProjectAudio(string locale)
    {
        _ = ForLocale(locale);
        return Array.AsReadOnly(Audio.Select(cue => cue.ForLocale(locale)).ToArray());
    }

    public ConstellationContent ForLocale(string locale) => LocalizedCatalog.ForLocale(locale, _enUS, _ptBR);

    public ConstellationView? Project(GameSession<ConstellationState, ConstellationAction> session,
        ParticipantId viewer, string locale)
    {
        if (!session.Participants.Contains(viewer))
        {
            return null;
        }

        if (session.Experience.ContentVersion != ContentVersion)
        {
            throw new ArgumentException("Pinned content version mismatch.");
        }

        var content = ForLocale(locale);
        var view = new ConstellationRules().Project(session, viewer);
        var cards = content.Cards.ToDictionary(card => card.CardId);
        var current = view.CurrentCard is null ? null : cards[view.CurrentCard.CardId];
        var revealed = session.State.Shared.Outcomes.OfType<CardOutcome.Revealed>();
        return view with
        {
            Title = content.Title,
            Objective = content.Objective,
            CurrentCard = current is null ? null : new CardView(current.CardId, view.CurrentCard!.Position, current.Prompt,
                current.Options.Select(option => new CardOptionView(option.OptionId, option.Label)).ToArray()),
            MyAnswer = view.MyAnswer?.Option is not { } own ? view.MyAnswer : view.MyAnswer with
            { Option = new CardOptionView(own.OptionId, current!.FindOption(own.OptionId)!.Label) },
            Revealed = revealed.Select(item => new RevealedCardView(item.Card.CardId, cards[item.Card.CardId].Prompt,
                cards[item.Card.CardId].FindOption(item.ChoiceByParticipant[viewer].OptionId)!.Label,
                cards[item.Card.CardId].FindOption(item.ChoiceByParticipant[session.Participants.PartnerOf(viewer)].OptionId)!.Label,
                item.ChoiceByParticipant[viewer].OptionId == item.ChoiceByParticipant[session.Participants.PartnerOf(viewer)].OptionId)).ToArray(),
            Passed = view.Passed.Select(item => new PassedCardView(item.CardId, cards[item.CardId].Prompt)).ToArray(),
        };
    }

    private static IEnumerable<string> Structure(ConstellationContent content) => content.Cards
        .SelectMany(card => new[] { "card:" + card.CardId }.Concat(card.Options.Select(option => "option:" + option.OptionId)));
}
