using Duora.Core.Jobs;

namespace Duora.UnitTests.Jobs;

public sealed class JitteredIntervalTests
{
    private static readonly TimeSpan ThirtySeconds = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0.0, 27_000)]
    [InlineData(0.25, 28_500)]
    [InlineData(0.5, 30_000)]
    [InlineData(0.75, 31_500)]
    [InlineData(0.999, 32_994)]
    public void NextDelaySpreadsSymmetricallyAroundThePeriod(double sample, int expectedMilliseconds)
    {
        var interval = new JitteredInterval(ThirtySeconds, jitterFraction: 0.1);

        var delay = interval.NextDelay(sample);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.999)]
    public void NextDelayIsExactlyThePeriodWhenJitterIsZero(double sample)
    {
        var interval = new JitteredInterval(ThirtySeconds, jitterFraction: 0);

        Assert.Equal(ThirtySeconds, interval.NextDelay(sample));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void NextDelayRejectsSampleOutsideUnitInterval(double sample)
    {
        var interval = new JitteredInterval(ThirtySeconds, jitterFraction: 0.1);

        Assert.Throws<ArgumentOutOfRangeException>(() => interval.NextDelay(sample));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositivePeriod(int periodMilliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JitteredInterval(TimeSpan.FromMilliseconds(periodMilliseconds), jitterFraction: 0.1));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void ConstructorRejectsJitterFractionOutsideZeroToOne(double jitterFraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JitteredInterval(ThirtySeconds, jitterFraction));
    }

    [Fact]
    public void ConstructorAcceptsTheSmallestPositivePeriodAndJitterJustBelowOne()
    {
        var interval = new JitteredInterval(TimeSpan.FromTicks(1), jitterFraction: 0.999);

        Assert.Equal(TimeSpan.FromTicks(1), interval.Period);
    }
}
