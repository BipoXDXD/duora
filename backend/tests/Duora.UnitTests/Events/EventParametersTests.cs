using Duora.Core.Events;

namespace Duora.UnitTests.Events;

public sealed class EventParametersTests
{
    [Fact]
    public void KeepsTheSnapshotValuesItWasCreatedWith()
    {
        var parameters = EventFixtures.Parameters;

        Assert.Equal(EventFixtures.ParameterSetId, parameters.ParameterSetId);
        Assert.Equal(1, parameters.Version);
        Assert.Equal(100, parameters.MaxParticipants);
        Assert.Equal(3, parameters.RoundCount);
        Assert.Equal(TimeSpan.FromMinutes(12), parameters.RoundWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), parameters.CheckInLeadTime);
        Assert.Equal(TimeSpan.FromMinutes(1), parameters.RoundStartTolerance);
    }

    [Fact]
    public void RejectsEmptyParameterSetId()
    {
        Assert.Throws<ArgumentException>(() => Build(parameterSetId: Guid.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveVersion(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(version: version));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveMaxParticipants(int maxParticipants)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(maxParticipants: maxParticipants));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveRoundCount(int roundCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(roundCount: roundCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveRoundWindow(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(roundWindow: TimeSpan.FromMinutes(minutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveCheckInLeadTime(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(checkInLeadTime: TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void RejectsNegativeRoundStartTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(roundStartTolerance: TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void AcceptsZeroRoundStartTolerance()
    {
        var parameters = Build(roundStartTolerance: TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, parameters.RoundStartTolerance);
    }

    [Fact]
    public void RejectsRoundStartToleranceThatReachesTheEndOfTheWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Build(roundWindow: TimeSpan.FromMinutes(12), roundStartTolerance: TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public void AcceptsRoundStartToleranceJustBelowTheWindow()
    {
        var tolerance = TimeSpan.FromMinutes(12) - TimeSpan.FromTicks(1);

        var parameters = Build(roundWindow: TimeSpan.FromMinutes(12), roundStartTolerance: tolerance);

        Assert.Equal(tolerance, parameters.RoundStartTolerance);
    }

    private static EventParameters Build(
        Guid? parameterSetId = null,
        int version = 1,
        int maxParticipants = 100,
        int roundCount = 3,
        TimeSpan? roundWindow = null,
        TimeSpan? checkInLeadTime = null,
        TimeSpan? roundStartTolerance = null) =>
        new(
            parameterSetId ?? EventFixtures.ParameterSetId,
            version,
            maxParticipants,
            roundCount,
            roundWindow ?? TimeSpan.FromMinutes(12),
            checkInLeadTime ?? TimeSpan.FromMinutes(10),
            roundStartTolerance ?? TimeSpan.FromMinutes(1));
}
