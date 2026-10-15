using Duora.Core.Events;

namespace Duora.UnitTests.Events;

public sealed class EventScheduleTests
{
    [Fact]
    public void CheckInOpensTheLeadTimeBeforeTheStart()
    {
        var schedule = new EventSchedule(EventFixtures.StartsAt, EventFixtures.Parameters);

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 50, 0, TimeSpan.Zero), schedule.CheckInOpensAt);
    }

    [Fact]
    public void EndsAfterAllRoundWindows()
    {
        var schedule = new EventSchedule(EventFixtures.StartsAt, EventFixtures.Parameters);

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 22, 36, 0, TimeSpan.Zero), schedule.EndsAt);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 12)]
    [InlineData(3, 24)]
    public void EachRoundStartsOneWindowAfterThePrevious(int roundNumber, int minutesAfterStart)
    {
        var schedule = new EventSchedule(EventFixtures.StartsAt, EventFixtures.Parameters);

        Assert.Equal(EventFixtures.StartsAt.AddMinutes(minutesAfterStart), schedule.RoundStartsAt(roundNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsRoundNumberOutsideTheSnapshot(int roundNumber)
    {
        var schedule = new EventSchedule(EventFixtures.StartsAt, EventFixtures.Parameters);

        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.RoundStartsAt(roundNumber));
    }

    [Fact]
    public void RejectsMissingParameters()
    {
        Assert.Throws<ArgumentNullException>(() => new EventSchedule(EventFixtures.StartsAt, null!));
    }
}
