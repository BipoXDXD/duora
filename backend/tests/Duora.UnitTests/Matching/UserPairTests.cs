using Duora.Core.Matching;
using static Duora.UnitTests.Matching.RoundBuilder;

namespace Duora.UnitTests.Matching;

public sealed class UserPairTests
{
    [Fact]
    public void NormalizesTheOrderOfTheTwoUsers()
    {
        Assert.Equal(UserPair.Of(User(1), User(2)), UserPair.Of(User(2), User(1)));
    }

    /// <summary>
    /// O par vai para <c>CHECK (user_low &lt; user_high)</c> no PostgreSQL, que compara <c>uuid</c> byte a byte, como
    /// a forma textual. Um primeiro grupo acima de 0x7fffffff pega uma comparação com sinal no lado .NET.
    /// </summary>
    [Theory]
    [InlineData("80000000-0000-0000-0000-000000000000", "7fffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("00000000-8000-0000-0000-000000000000", "00000000-7fff-0000-0000-000000000000")]
    [InlineData("00000000-0000-8000-0000-000000000000", "00000000-0000-7fff-0000-000000000000")]
    [InlineData("00000000-0000-0000-8000-000000000000", "00000000-0000-0000-7fff-ffffffffffff")]
    [InlineData("00000000-0000-0000-0000-800000000000", "00000000-0000-0000-0000-7fffffffffff")]
    public void LowUserFollowsThePostgreSqlUuidOrder(string higher, string lower)
    {
        var pair = UserPair.Of(Guid.Parse(higher), Guid.Parse(lower));

        Assert.Equal(Guid.Parse(lower), pair.UserLow);
        Assert.Equal(Guid.Parse(higher), pair.UserHigh);
    }

    [Fact]
    public void RejectsThePairOfAUserWithThemself()
    {
        Assert.Throws<ArgumentException>(() => UserPair.Of(User(1), User(1)));
    }

    [Fact]
    public void RejectsEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() => UserPair.Of(Guid.Empty, User(1)));
    }
}
