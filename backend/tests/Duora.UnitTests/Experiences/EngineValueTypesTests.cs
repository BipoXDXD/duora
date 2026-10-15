using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>Tipos de valor do motor: nascem válidos ou não nascem.</summary>
public sealed class EngineValueTypesTests
{
    [Fact]
    public void PairNeedsTwoDifferentParticipants()
    {
        Assert.Throws<ArgumentException>(() => new ParticipantPair(Ana, Ana));
    }

    [Fact]
    public void PairKnowsItsMembersAndThePartnerOfEach()
    {
        var pair = new ParticipantPair(Ana, Bia);

        Assert.True(pair.Contains(Ana));
        Assert.True(pair.Contains(Bia));
        Assert.False(pair.Contains(Outsider));
        Assert.Equal(Bia, pair.PartnerOf(Ana));
        Assert.Equal(Ana, pair.PartnerOf(Bia));
    }

    [Fact]
    public void PartnerOfSomeoneOutsideThePairIsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => new ParticipantPair(Ana, Bia).PartnerOf(Outsider));
    }

    [Fact]
    public void IdentifiersRejectTheEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new ParticipantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ActionId(Guid.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StateVersionStartsAtOne(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateVersion(value));
    }

    [Fact]
    public void StateVersionNextIsOneAbove()
    {
        Assert.Equal(new StateVersion(8), new StateVersion(7).Next());
        Assert.Equal(new StateVersion(1), StateVersion.Initial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unlikely")]
    [InlineData("rotas improváveis")]
    public void ExperienceIdIsALowercaseSlug(string value)
    {
        Assert.Throws<ArgumentException>(() => new ExperienceId(value));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void ExperienceVersionNumbersArePositive(int rules, int content, int schema)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExperienceVersion(new ExperienceId("unlikely-routes"), rules, content, schema));
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(8, 0)]
    [InlineData(-1, 9)]
    public void TimeLimitsArePositive(int maxPlayMinutes, int maxAfterRoundStartMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameTimeLimits(
            TimeSpan.FromMinutes(maxPlayMinutes), TimeSpan.FromMinutes(maxAfterRoundStartMinutes)));
    }

    [Fact]
    public void DefaultTimeLimitsComeFromTheSpec()
    {
        Assert.Equal(new GameTimeLimits(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(9)), GameTimeLimits.Default);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(UnlikelyRoutesSettings.MaxRevisionsCeiling + 1)]
    public void RevisionLimitStaysWithinItsRange(int maxRevisions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnlikelyRoutesSettings(maxRevisions));
    }

    [Fact]
    public void DescriptorListsTheClosedSetOfActionTypes()
    {
        var descriptor = new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1)).Descriptor;

        Assert.Equal(
            ["acknowledge", "choose-option", "confirm-route", "request-revision"],
            descriptor.ActionTypes.Select(type => type.Value));
        Assert.Equal(new ExperienceId("unlikely-routes"), descriptor.ExperienceId);
        Assert.Equal(1, descriptor.RulesVersion);
        Assert.Equal(1, descriptor.SchemaVersion);
    }

    [Fact]
    public void EachActionDeclaresATypeFromTheDescriptor()
    {
        var descriptor = new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1)).Descriptor;
        UnlikelyRoutesAction[] actions =
        [
            new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction),
            new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"),
            new UnlikelyRoutesAction.ConfirmRoute(),
            new UnlikelyRoutesAction.RequestRevision(),
        ];

        Assert.Equal(descriptor.ActionTypes, actions.Select(action => action.Type));
    }

    [Fact]
    public void CommandRequiresAnAction()
    {
        Assert.Throws<ArgumentNullException>(() => new GameCommand<UnlikelyRoutesAction>(
            new ActionId(Guid.NewGuid()), Ana, StateVersion.Initial, null!));
    }
}
