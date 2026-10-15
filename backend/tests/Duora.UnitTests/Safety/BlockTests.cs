using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class BlockTests
{
    [Fact]
    public void CreateRecordsWhoBlockedWhomAndWhen()
    {
        var block = Block.Create(Alice, Bruno, FixedClock.AtStart()).Accepted();

        Assert.Equal(Alice, block.Pair.BlockerId);
        Assert.Equal(Bruno, block.Pair.BlockedId);
        Assert.Equal(Start, block.CreatedAt);
    }

    [Fact]
    public void SelfBlockIsRejected()
    {
        SafetyAssert.Rejected(SafetyRejection.SelfBlock, Block.Create(Alice, Alice, FixedClock.AtStart()));
    }

    [Fact]
    public void APairOfTheSamePersonCannotExist()
    {
        Assert.Throws<ArgumentException>(() => new BlockPair(Alice, Alice));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PairRejectsEmptyUserId(bool emptyBlocker)
    {
        var blocker = emptyBlocker ? Guid.Empty : Alice;
        var blocked = emptyBlocker ? Bruno : Guid.Empty;

        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPair(blocker, blocked));
    }

    [Theory]
    [InlineData("a", "b", true)]
    [InlineData("b", "a", true)]
    [InlineData("a", "c", false)]
    [InlineData("c", "b", false)]
    [InlineData("c", "a", false)]
    public void PairSeparatesItsTwoPeopleInEitherOrder(string first, string second, bool expected)
    {
        var pair = new BlockPair(Alice, Bruno);

        Assert.Equal(expected, pair.Separates(Person(first), Person(second)));
    }

    [Fact]
    public void LiftByTheBlockerProducesOnlyTheFactOfRemoval()
    {
        var clock = FixedClock.AtStart();
        var block = Block.Create(Alice, Bruno, clock).Accepted();
        clock.Advance(TimeSpan.FromHours(1));

        var lifted = block.Lift(Alice, clock);

        Assert.Equal(new BlockLifted(Alice, Bruno, Start.AddHours(1)), lifted);
    }

    [Fact]
    public void BlockLiftedCarriesNothingThatCouldRestoreTheRelationship()
    {
        // Desbloquear não reabre conexão, conversa, decisão nem sala (spec 7.3, TM-27): o fato só identifica o par.
        var properties = typeof(BlockLifted).GetProperties().Select(property => property.Name).Order();

        Assert.Equal(["BlockedId", "BlockerId", "LiftedAt"], properties);
    }

    [Fact]
    public void TheBlockedPersonCannotLiftTheBlock()
    {
        var block = Block.Create(Alice, Bruno, FixedClock.AtStart()).Accepted();

        var violation = Assert.Throws<SafetyRuleViolationException>(() => block.Lift(Bruno, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.OnlyBlockerCanLift, violation.Rule);
    }

    private static Guid Person(string letter) => letter switch
    {
        "a" => Alice,
        "b" => Bruno,
        "c" => Carla,
        _ => throw new ArgumentOutOfRangeException(nameof(letter)),
    };
}
