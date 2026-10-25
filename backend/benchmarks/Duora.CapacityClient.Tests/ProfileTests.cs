namespace Duora.CapacityClient.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void MissingQualificationNeverCreatesATransport()
    {
        var profile = new LoadProfile();
        var error = Assert.Throws<LoadRejectedException>(() => profile.Validate(DateTimeOffset.UtcNow));
        Assert.Equal(LoadStopReason.Profile, error.Reason);
    }

    [Fact]
    public void CompleteWorkloadCannotBeAdmittedByAnHttpOrNotificationClient()
    {
        var profile = new LoadProfile { Coverage = "complete-c2" };
        Assert.Throws<LoadRejectedException>(() => profile.Validate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SequenceReceiptDoesNotTurnDuplicatesGapsOrDisorderIntoSuccess()
    {
        var sequence = new SequenceReceipt();
        sequence.Observe(1);
        sequence.Observe(2);
        sequence.Observe(2);
        sequence.Observe(4);
        sequence.Observe(3);
        Assert.Equal(5, sequence.Received);
        Assert.Equal(1, sequence.Gaps);
        Assert.Equal(1, sequence.Duplicates);
        Assert.Equal(1, sequence.OutOfOrder);
    }

    [Fact]
    public void ScheduleRequiresRealWarmupRampSteadyBurstReconnectAndRecoveryDurations()
    {
        Assert.Throws<LoadRejectedException>(() => PhasePlan.Validate([]));
        var plan = PhasePlan.Required();
        PhasePlan.Validate(plan);
        plan[0] = plan[0] with { Seconds = 1 };
        Assert.Throws<LoadRejectedException>(() => PhasePlan.Validate(plan));
    }
}
