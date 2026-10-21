namespace Duora.Core.Experiences.UnlikelyRoutes;

public sealed class LocalizedUnlikelyRoutesContent : IExperienceContent
{
    private readonly UnlikelyRoutesContent _enUS;
    private readonly UnlikelyRoutesContent _ptBR;

    public LocalizedUnlikelyRoutesContent(UnlikelyRoutesContent enUS, UnlikelyRoutesContent ptBR,
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

    public UnlikelyRoutesContent ForLocale(string locale) => LocalizedCatalog.ForLocale(locale, _enUS, _ptBR);

    public UnlikelyRoutesView? Project(GameSession<UnlikelyRoutesState, UnlikelyRoutesAction> session,
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
        var view = new UnlikelyRoutesRules(new UnlikelyRoutesSettings(session.State.Control.MaxRevisions)).Project(session, viewer);
        var source = session.State.Control.Content;
        var ownClues = session.State.PrivateByParticipant[viewer].Clues;
        var clueSet = source.ClueSets.Select((set, index) => (set, index)).Single(item => item.set.SequenceEqual(ownClues)).index;
        var stop = view.CurrentStop is null ? null : content.Stops.Single(item => item.StepId == view.CurrentStop.StepId);
        return view with
        {
            Title = content.Title,
            Objective = content.Objective,
            Example = content.Example,
            MyClues = view.MyClues.Count == 0 ? [] : content.ClueSets[clueSet].Select(clue => clue.Text).ToArray(),
            CurrentStop = stop is null ? null : new RouteStopView(stop.StepId, stop.Prompt,
                stop.Options.Select(option => new RouteOptionView(option.OptionId, option.Label)).ToArray()),
            Route = session.State.Shared.ChosenOptions.Select((option, index) => new ChosenStopView(content.Stops[index].StepId,
                content.Stops[index].Prompt, content.Stops[index].FindOption(option.OptionId)!.Label)).ToArray(),
        };
    }

    private static IEnumerable<string> Structure(UnlikelyRoutesContent content) => content.Stops
        .SelectMany(stop => new[] { "stop:" + stop.StepId }.Concat(stop.Options.Select(option => "option:" + option.OptionId)))
        .Concat(content.ClueSets.Select(set => "clues:" + set.Count));
}
