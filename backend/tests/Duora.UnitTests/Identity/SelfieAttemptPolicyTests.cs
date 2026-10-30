using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Regra do dono (issue #300, 29/10/2026): cinco reprovações fecham o primeiro ciclo por 1 hora; depois, mais cinco;
/// a décima reprovação esgota as tentativas. Valores esperados são literais, calculados à mão.
/// </summary>
public sealed class SelfieAttemptPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(1, 4, false)]
    [InlineData(4, 1, false)]
    [InlineData(6, 4, true)]
    [InlineData(9, 1, true)]
    public void AvailableAttemptsCountDownWithinTheCycle(int failures, int remaining, bool lastCycle)
    {
        var standing = SelfieAttemptPolicy.Evaluate(Failures(failures), Start.AddHours(3));

        Assert.Equal(new SelfieAttemptStanding.Available(remaining, lastCycle), standing);
    }

    [Fact]
    public void TheFifthFailureLocksForOneHourFromThatFailure()
    {
        var failures = Failures(5);

        var standing = SelfieAttemptPolicy.Evaluate(failures, Start.AddMinutes(4));

        Assert.Equal(new SelfieAttemptStanding.CoolingDown(Start.AddMinutes(64)), standing);
    }

    [Fact]
    public void OneTickBeforeTheHourTheSelfieIsStillLocked()
    {
        var standing = SelfieAttemptPolicy.Evaluate(Failures(5), Start.AddMinutes(64).AddTicks(-1));

        Assert.Equal(new SelfieAttemptStanding.CoolingDown(Start.AddMinutes(64)), standing);
    }

    [Fact]
    public void AtExactlyOneHourTheSecondCycleOpensWithFiveAttempts()
    {
        var standing = SelfieAttemptPolicy.Evaluate(Failures(5), Start.AddMinutes(64));

        Assert.Equal(new SelfieAttemptStanding.Available(5, LastCycle: true), standing);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void TheTenthFailureExhaustsTheAttemptsForGood(int failures)
    {
        var standing = SelfieAttemptPolicy.Evaluate(Failures(failures), Start.AddDays(400));

        Assert.Equal(new SelfieAttemptStanding.Exhausted(), standing);
    }

    [Fact]
    public void TheLockFollowsTheFifthFailureInTimeNotTheOrderOfTheList()
    {
        DateTimeOffset[] failures =
        [
            Start.AddMinutes(4), Start, Start.AddMinutes(2), Start.AddMinutes(1), Start.AddMinutes(3),
        ];

        var standing = SelfieAttemptPolicy.Evaluate(failures, Start.AddMinutes(5));

        Assert.Equal(new SelfieAttemptStanding.CoolingDown(Start.AddMinutes(64)), standing);
    }

    [Fact]
    public void TheLockCountsFromTheFifthFailureEvenWithSixRecorded()
    {
        var standing = SelfieAttemptPolicy.Evaluate(Failures(6), Start.AddMinutes(30));

        Assert.Equal(new SelfieAttemptStanding.CoolingDown(Start.AddMinutes(64)), standing);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void OnlyTheTenthFailureAsksForTheSuspension(int failures, bool suspends)
        => Assert.Equal(suspends, SelfieAttemptPolicy.ExhaustsAttempts(failures));

    [Theory]
    [InlineData(SelfieState.RecaptureRequired, true)]
    [InlineData(SelfieState.Inconclusive, true)]
    [InlineData(SelfieState.Passed, false)]
    [InlineData(SelfieState.Expired, false)]
    [InlineData(SelfieState.Pending, false)]
    [InlineData(SelfieState.Capturing, false)]
    [InlineData(SelfieState.Processing, false)]
    public void OnlyAFinalRejectionFromTheProviderCounts(SelfieState state, bool counts)
        => Assert.Equal(counts, SelfieAttemptPolicy.CountsAsFailure(new SelfieResult(state, null, "policy-a"), "policy-a"));

    [Theory]
    [InlineData(SelfieState.RecaptureRequired)]
    [InlineData(SelfieState.Inconclusive)]
    public void AResultUnderAnotherPolicyVersionIsATechnicalMismatchAndDoesNotCount(SelfieState state)
        => Assert.False(SelfieAttemptPolicy.CountsAsFailure(new SelfieResult(state, null, "policy-b"), "policy-a"));

    [Fact]
    public void NoFailuresMeansAFullFirstCycle()
        => Assert.Equal(
            new SelfieAttemptStanding.Available(5, LastCycle: false),
            SelfieAttemptPolicy.Evaluate([], Start));

    private static DateTimeOffset[] Failures(int count) =>
        [.. Enumerable.Range(0, count).Select(minute => Start.AddMinutes(minute))];
}
