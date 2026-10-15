using Duora.Core.Jobs;

namespace Duora.UnitTests.Jobs;

public sealed class ExponentialBackoffTests
{
    private static readonly ExponentialBackoff Backoff = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

    [Theory]
    [InlineData(1, 0.0, 15)]
    [InlineData(1, 0.5, 22.5)]
    [InlineData(2, 0.0, 30)]
    [InlineData(3, 0.0, 60)]
    [InlineData(5, 0.999, 479.76)]
    public void DelayDoublesPerAttemptWithJitterInTheUpperHalf(int attempt, double sample, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Backoff.DelayAfter(attempt, sample));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(60)]
    public void DelayNeverPassesTheCap(int attempt)
    {
        Assert.InRange(Backoff.DelayAfter(attempt, sample: 0.999_999), TimeSpan.FromMinutes(14.99), TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(7.5), Backoff.DelayAfter(attempt, sample: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsAttemptBelowOne(int attempt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.DelayAfter(attempt, 0.5));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void RejectsSampleOutsideUnitInterval(double sample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.DelayAfter(1, sample));
    }

    [Fact]
    public void RejectsCapBelowInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExponentialBackoff(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)));
    }
}
