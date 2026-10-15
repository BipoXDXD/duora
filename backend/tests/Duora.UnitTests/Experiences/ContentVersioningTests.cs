using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>
/// INV-09 no domínio: a sessão guarda a versão de conteúdo com que nasceu e projeta o conteúdo dessa versão. A T-09
/// completa (outra versão publicada no catálogo persistido durante a partida) é teste de integração do M2.
/// </summary>
public sealed class ContentVersioningTests
{
    private readonly UnlikelyRoutesEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void SessionKeepsTheContentVersionItWasCreatedWithAcrossActions()
    {
        var session = NewSession(_engine, Content(contentVersion: 1, title: "Um passeio por Vila Inventada"));

        session = AtChoices(_engine, session);

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(1, session.Experience.ContentVersion);
        Assert.Equal(1, view.ContentVersion);
        Assert.Equal("Um passeio por Vila Inventada", view.Title);
    }

    [Fact]
    public void SessionsOfDifferentContentVersionsProjectTheirOwnContent()
    {
        var older = NewSession(_engine, Content(contentVersion: 1, title: "Um passeio por Vila Inventada"));
        var newer = NewSession(_engine, Content(contentVersion: 2, title: "Um passeio por Vila Revisada"));

        older = Act(_engine, older, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));

        var olderView = _engine.ProjectForParticipant(older, Ana)!;
        var newerView = _engine.ProjectForParticipant(newer, Ana)!;
        Assert.Equal((1, "Um passeio por Vila Inventada"), (olderView.ContentVersion, olderView.Title));
        Assert.Equal((2, "Um passeio por Vila Revisada"), (newerView.ContentVersion, newerView.Title));
    }

    [Fact]
    public void SessionRecordsEveryVersionOfTheExperience()
    {
        var session = NewSession(_engine, Content(contentVersion: 3, title: "Terceira"));

        Assert.Equal(
            new ExperienceVersion(new ExperienceId("unlikely-routes"), rulesVersion: 1, contentVersion: 3, schemaVersion: 1),
            session.Experience);
    }

    [Fact]
    public void SessionCannotBeCreatedFromContentOfAnotherExperience()
    {
        var foreign = new ForeignContent(new ExperienceId("choice-constellation"), 1);
        var engine = new ExperienceEngine<ForeignContent, UnlikelyRoutesState, UnlikelyRoutesAction, UnlikelyRoutesView>(
            new ForeignRulesAdapter(), ClockAtRoundStart());

        Assert.Throws<ArgumentException>(() => engine.CreateSession(
            foreign, new ParticipantPair(Ana, Bia), RoundStart, GameTimeLimits.Default, AnaFirstSeed));
    }

    private sealed record ForeignContent(ExperienceId ExperienceId, int ContentVersion) : IExperienceContent;

    /// <summary>Regras de A recebendo conteúdo com outro <see cref="ExperienceId"/>: o motor precisa recusar.</summary>
    private sealed class ForeignRulesAdapter
        : IExperienceRules<ForeignContent, UnlikelyRoutesState, UnlikelyRoutesAction, UnlikelyRoutesView>
    {
        private readonly UnlikelyRoutesRules _inner = new(new UnlikelyRoutesSettings(1));

        public ExperienceDescriptor Descriptor => _inner.Descriptor;

        public UnlikelyRoutesState CreateInitialState(ForeignContent content, ParticipantPair participants, SessionSeed seed) =>
            _inner.CreateInitialState(ContentV1(), participants, seed);

        public RejectionReason? Validate(UnlikelyRoutesState state, ParticipantId actor, UnlikelyRoutesAction action) =>
            _inner.Validate(state, actor, action);

        public RulesTransition<UnlikelyRoutesState> Apply(
            UnlikelyRoutesState state, ParticipantId actor, UnlikelyRoutesAction action) =>
            _inner.Apply(state, actor, action);

        public RulesTransition<UnlikelyRoutesState> ResolveDeadline(UnlikelyRoutesState state) =>
            _inner.ResolveDeadline(state);

        public UnlikelyRoutesView Project(
            GameSession<UnlikelyRoutesState, UnlikelyRoutesAction> session, ParticipantId viewer) =>
            _inner.Project(session, viewer);

        public AccessibleInteraction DescribeAccessibleInteraction() => _inner.DescribeAccessibleInteraction();
    }
}
