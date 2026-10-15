using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class InteractionPolicyTests
{
    private static readonly Block[] NoBlocks = [];
    private static readonly ModerationAction[] NoSanctions = [];

    [Fact]
    public void PeopleWithoutBlockOrSanctionCanInteract()
    {
        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, NoSanctions, FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ABlockInEitherDirectionStopsInteractionForBothPeople(bool aliceBlocked, bool askedFromAliceSide)
    {
        var block = aliceBlocked
            ? Block.Create(Alice, Bruno, FixedClock.AtStart())
            : Block.Create(Bruno, Alice, FixedClock.AtStart());
        var (a, b) = askedFromAliceSide ? (Alice, Bruno) : (Bruno, Alice);

        var verdict = InteractionPolicy.CheckInteraction(a, b, [block], NoSanctions, FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Unavailable, verdict);
    }

    [Fact]
    public void ABlockBetweenOtherPeopleDoesNotAffectThePair()
    {
        Block[] blocks = [Block.Create(Alice, Carla, FixedClock.AtStart()), Block.Create(Carla, Bruno, FixedClock.AtStart())];

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, blocks, NoSanctions, FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Fact]
    public void LiftingOneDirectionKeepsTheOtherPersonsBlockInForce()
    {
        var aliceBlocksBruno = Block.Create(Alice, Bruno, FixedClock.AtStart());
        var brunoBlocksAlice = Block.Create(Bruno, Alice, FixedClock.AtStart());
        aliceBlocksBruno.Lift(Alice, FixedClock.AtStart());

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, [brunoBlocksAlice], NoSanctions, FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Unavailable, verdict);
    }

    [Fact]
    public void AfterUnblockOnlyNewInteractionsAreEvaluatedAgain()
    {
        // O bloqueio removido deixa de contar para checagens futuras; o que ele encerrou fica encerrado em
        // Connections/Messaging, porque BlockLifted não carrega intenção de restaurar (ver BlockTests).
        var clock = FixedClock.AtStart();
        var block = Block.Create(Alice, Bruno, clock);
        block.Lift(Alice, clock);

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, NoSanctions, clock);

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Fact]
    public void VerdictIsNeutralAndNeverSaysWhoBlocked()
    {
        Assert.Equal([InteractionVerdict.Allowed, InteractionVerdict.Unavailable], Enum.GetValues<InteractionVerdict>());
    }

    [Theory]
    [InlineData(ModerationMeasure.Restriction, true)]
    [InlineData(ModerationMeasure.Suspension, true)]
    [InlineData(ModerationMeasure.PermanentBan, true)]
    [InlineData(ModerationMeasure.Restriction, false)]
    [InlineData(ModerationMeasure.Suspension, false)]
    [InlineData(ModerationMeasure.PermanentBan, false)]
    public void ARestrictiveSanctionOnEitherPersonStopsInteraction(ModerationMeasure measure, bool onAlice)
    {
        var sanction = SanctionOn(onAlice ? Alice : Bruno, measure);

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, [sanction], FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Unavailable, verdict);
    }

    [Fact]
    public void AWarningDoesNotStopInteraction()
    {
        var verdict = InteractionPolicy.CheckInteraction(
            Alice, Bruno, NoBlocks, [SanctionOn(Alice, ModerationMeasure.Warning)], FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Fact]
    public void ASanctionOnSomeoneElseDoesNotAffectThePair()
    {
        var verdict = InteractionPolicy.CheckInteraction(
            Alice, Bruno, NoBlocks, [SanctionOn(Carla)], FixedClock.AtStart());

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Theory]
    [InlineData(-1, InteractionVerdict.Unavailable)]
    [InlineData(0, InteractionVerdict.Allowed)]
    public void ATemporarySanctionEndsExactlyAtItsEffectiveUntil(long ticksFromEnd, InteractionVerdict expected)
    {
        var clock = FixedClock.AtStart();
        var suspension = SanctionOn(Alice, ModerationMeasure.Suspension, clock);
        clock.Advance(OneWeek + TimeSpan.FromTicks(ticksFromEnd));

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, [suspension], clock);

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void ASanctionOverturnedOnAppealNoLongerStopsInteraction()
    {
        var clock = FixedClock.AtStart();
        var suspension = SanctionOn(Alice, ModerationMeasure.Suspension, clock);
        var appeal = Appeal.File(suspension, Alice, "Não fui eu.", clock);
        appeal.StartReview(ModeratorPedro);
        var revision = appeal.Overturn(ModeratorPedro, "Evidência insuficiente.", RevisedPolicy, clock);

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, [suspension, revision], clock);

        Assert.Equal(InteractionVerdict.Allowed, verdict);
    }

    [Fact]
    public void ASanctionUpheldOnAppealKeepsStoppingInteraction()
    {
        var clock = FixedClock.AtStart();
        var suspension = SanctionOn(Alice, ModerationMeasure.Suspension, clock);
        var appeal = Appeal.File(suspension, Alice, "Não fui eu.", clock);
        appeal.StartReview(ModeratorPedro);
        var revision = appeal.Uphold(ModeratorPedro, "Evidência confirmada.", RevisedPolicy, clock);

        var verdict = InteractionPolicy.CheckInteraction(Alice, Bruno, NoBlocks, [suspension, revision], clock);

        Assert.Equal(InteractionVerdict.Unavailable, verdict);
    }

    [Fact]
    public void APersonCannotBeCheckedAgainstThemselves()
    {
        Assert.Throws<ArgumentException>(
            () => InteractionPolicy.CheckInteraction(Alice, Alice, NoBlocks, NoSanctions, FixedClock.AtStart()));
    }
}
