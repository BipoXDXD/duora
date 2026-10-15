using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>INV-09 e catálogo versionado e imutável (spec 6.1, 6.3).</summary>
public sealed class ContentVersioningTests
{
    private readonly UnlikelyRoutesEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void SessionKeepsItsContentVersionAfterANewVersionIsPublished()
    {
        var catalog = new ContentCatalog<UnlikelyRoutesContent>().Publish(ContentV1());
        var session = NewSession(_engine, catalog.Latest);

        catalog = catalog.Publish(Content(contentVersion: 2, title: "Um passeio por Vila Revisada"));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(2, catalog.Latest.ContentVersion);
        Assert.Equal(1, session.Experience.ContentVersion);
        Assert.Equal(1, view.ContentVersion);
        Assert.Equal("Um passeio por Vila Inventada", view.Title);
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
    public void CatalogFindsEachPublishedVersion()
    {
        var catalog = new ContentCatalog<UnlikelyRoutesContent>()
            .Publish(ContentV1())
            .Publish(Content(contentVersion: 2, title: "Segunda"));

        Assert.Equal("Um passeio por Vila Inventada", catalog.Find(1)!.Title);
        Assert.Equal("Segunda", catalog.Find(2)!.Title);
        Assert.Null(catalog.Find(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PublishedVersionCannotBeReplacedOrPrecededLater(int contentVersion)
    {
        var catalog = new ContentCatalog<UnlikelyRoutesContent>()
            .Publish(ContentV1())
            .Publish(Content(contentVersion: 3, title: "Terceira"));

        Assert.Throws<ArgumentException>(() => catalog.Publish(Content(contentVersion, "Outra")));
    }

    [Fact]
    public void PublishingReturnsANewCatalogAndLeavesTheOriginalUntouched()
    {
        var original = new ContentCatalog<UnlikelyRoutesContent>().Publish(ContentV1());

        original.Publish(Content(contentVersion: 2, title: "Segunda"));

        Assert.Equal(1, original.Latest.ContentVersion);
    }

    [Fact]
    public void CatalogHoldsASingleExperience()
    {
        var catalog = new ContentCatalog<IExperienceContent>().Publish(ContentV1());

        Assert.Throws<ArgumentException>(
            () => catalog.Publish(new ForeignContent(new ExperienceId("choice-constellation"), 2)));
    }

    [Fact]
    public void EmptyCatalogHasNoLatestVersion()
    {
        Assert.Throws<InvalidOperationException>(() => new ContentCatalog<UnlikelyRoutesContent>().Latest);
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
