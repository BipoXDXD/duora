using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParticipantSessionIsThirtyMinutesIdleAndTwelveHoursAbsolute()
    {
        var window = SessionLifetime.Participant.Open(Start);

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero), window.IdleExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), window.AbsoluteExpiresAt);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(30, 29)]
    public void RejectsNonPositiveIdleOrAbsoluteShorterThanIdle(int idleMinutes, int absoluteMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionLifetime(TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromMinutes(absoluteMinutes)));
    }
}
