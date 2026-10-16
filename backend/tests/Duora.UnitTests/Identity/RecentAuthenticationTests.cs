using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class RecentAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowIsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RecentAuthentication.Window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(299)]
    public void ProofInsideTheWindowIsRecent(int secondsAgo)
    {
        Assert.True(RecentAuthentication.IsRecent(Now.AddSeconds(-secondsAgo), Now));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    public void ProofAtOrPastTheEndOfTheWindowIsNotRecent(int secondsAgo)
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
        Assert.False(RecentAuthentication.IsRecent(Now.AddSeconds(1), Now));
    }
}
