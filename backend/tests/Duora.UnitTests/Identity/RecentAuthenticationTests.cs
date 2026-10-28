using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class RecentAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowIsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), RecentAuthentication.Window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3599)]
    [InlineData(3600)]
    public void ProofInsideTheWindowIsRecent(int secondsAgo)
    {
        Assert.True(RecentAuthentication.IsRecent(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void ProofExactlyAtTheEndOfTheWindowIsStillRecent()
    {
        Assert.True(RecentAuthentication.IsRecent(Now - RecentAuthentication.Window, Now));
    }

    [Fact]
    public void ProofOneTickPastTheEndOfTheWindowIsNotRecent()
    {
        Assert.False(RecentAuthentication.IsRecent(Now - RecentAuthentication.Window - TimeSpan.FromTicks(1), Now));
    }

    [Theory]
    [InlineData(3601)]
    [InlineData(86400)]
    public void ProofPastTheEndOfTheWindowIsNotRecent(int secondsAgo)
    {
        Assert.False(RecentAuthentication.IsRecent(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void MissingProofIsNotRecent()
    {
        Assert.False(RecentAuthentication.IsRecent(null, Now));
    }

    [Fact]
    public void ProofFromTheFutureIsNotRecent()
    {
        // Relógio que voltou ou dado adulterado: não concede privilégio.
        Assert.False(RecentAuthentication.IsRecent(Now.AddTicks(1), Now));
    }
}
