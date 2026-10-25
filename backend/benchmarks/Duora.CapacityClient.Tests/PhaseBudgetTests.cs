namespace Duora.CapacityClient.Tests;

public sealed class PhaseBudgetTests
{
    private const string Run = "11111111111111111111111111111111";
    private const string Source = "2222222222222222222222222222222222222222";
    private const string Dataset = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string Producer = "4444444444444444444444444444444444444444444444444444444444444444";

    [Fact]
    public void ExplicitPerRunHighPortTargetIsAcceptedButProductionAndLocalTargetsAreNot()
    {
        new TargetBinding(Run, "192.0.2.42", 45100, "qualification." + Run + ".invalid").Validate();
        foreach (var target in new[]
        {
            new TargetBinding(Run, "192.0.2.42", 443, "qualification." + Run + ".invalid"),
            new TargetBinding(Run, "127.0.0.1", 45100, "qualification." + Run + ".invalid"),
            new TargetBinding(Run, "169.254.169.254", 45100, "qualification." + Run + ".invalid"),
            new TargetBinding(Run, "10.0.0.1", 45100, "qualification." + Run + ".invalid"),
            new TargetBinding(Run, "192.0.2.42", 45100, "meetduora.com"),
        })
        {
            Assert.Throws<LoadRejectedException>(target.Validate);
        }
    }

    [Fact]
    public async Task CountBudgetIsConsumedExactlyOnceBeforeAnotherOperationIsAdmitted()
    {
        var clock = new ManualClock();
        var phase = Phase(clock, requests: 1);
        using var budget = Budget(phase, clock, () => Lease(phase, clock), TestContext.Current.CancellationToken);
        using (await budget.EnterAsync(TestContext.Current.CancellationToken))
        { }
        Assert.Equal(1, budget.StartedRequests);
        await Assert.ThrowsAsync<LoadRejectedException>(async () => { using var operation = await budget.EnterAsync(TestContext.Current.CancellationToken); });
        Assert.Equal(1, budget.StartedRequests);
    }

    [Fact]
    public async Task CancelledWaitDoesNotLeakSlotOrAdmitAnOperation()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        using var budget = Budget(phase, clock, () => Lease(phase, clock), TestContext.Current.CancellationToken);
        var first = await budget.EnterAsync(TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var pending = budget.EnterAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var operation = await pending; });
        Assert.Equal(1, budget.StartedRequests);
        first.Dispose();
        using (await budget.EnterAsync(TestContext.Current.CancellationToken))
        { }
        Assert.Equal(2, budget.StartedRequests);
    }

    [Fact]
    public async Task StopOrStaleOrDifferentRunLeasePreventsNewOperation()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        foreach (var lease in new[]
        {
            Lease(phase, clock) with { Stop = true },
            Lease(phase, clock) with { ObservedAt = clock.GetUtcNow().AddSeconds(-4) },
            Lease(phase, clock) with { RunId = "55555555555555555555555555555555" },
            Lease(phase, clock) with { SourceCommit = "6666666666666666666666666666666666666666" },
            Lease(phase, clock) with { PhaseStartedAt = phase.StartedAt.AddSeconds(1) },
        })
        {
            using var budget = Budget(phase, clock, () => lease, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<LoadRejectedException>(async () => { using var operation = await budget.EnterAsync(TestContext.Current.CancellationToken); });
            Assert.Equal(0, budget.StartedRequests);
        }
    }

    [Fact]
    public async Task MonotonicDeadlineDoesNotSlideWhenWallClockMovesBackwardsOrLeaseIsRenewed()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        using var budget = Budget(phase, clock, () => Lease(phase, clock), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(61));
        clock.MoveWallClock(TimeSpan.FromSeconds(-40));
        await Assert.ThrowsAsync<LoadRejectedException>(async () => { using var operation = await budget.EnterAsync(TestContext.Current.CancellationToken); });
        Assert.Equal(0, budget.StartedRequests);
    }

    [Fact]
    public async Task CallerCancellationIsObservedBeforeLeaseOrTransport()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        var reads = 0;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var budget = Budget(phase, clock, () => { reads++; return Lease(phase, clock); }, stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var operation = await budget.EnterAsync(TestContext.Current.CancellationToken); });
        Assert.Equal(0, reads);
        Assert.Equal(0, budget.StartedRequests);
    }

    [Fact]
    public async Task CleanupReserveCannotAdmitWorkOrExtendItsOwnDeadline()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        using var budget = Budget(phase, clock, () => Lease(phase, clock), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(61));
        await Assert.ThrowsAsync<LoadRejectedException>(async () => { using var operation = await budget.EnterAsync(TestContext.Current.CancellationToken); });
        using (budget.EnterCleanup())
        { }
        clock.Advance(TimeSpan.FromSeconds(60));
        clock.MoveWallClock(TimeSpan.FromSeconds(-60));
        Assert.Throws<LoadRejectedException>(budget.EnterCleanup);
        Assert.Equal(0, budget.StartedRequests);
    }

    [Fact]
    public void StopAlsoPreventsCleanupNetworkRequests()
    {
        var clock = new ManualClock();
        var phase = Phase(clock);
        using var budget = Budget(phase, clock, () => Lease(phase, clock) with { Stop = true }, TestContext.Current.CancellationToken);
        Assert.Throws<LoadRejectedException>(budget.EnterCleanup);
    }

    private static PhaseSpec Phase(ManualClock clock, int requests = 3) =>
        new(Run, "reference-1", clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(60), clock.GetUtcNow().AddSeconds(120), requests, 1);
    private static GuardLease Lease(PhaseSpec phase, ManualClock clock) =>
        new(Run, phase.PhaseId, phase.StartedAt, clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(2), Source, Dataset, Producer, false);
    private static PhaseBudget Budget(PhaseSpec phase, ManualClock clock, Func<GuardLease> lease, CancellationToken token) =>
        new(phase, Source, Dataset, Producer, lease, clock, token);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.Parse("2026-10-25T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) { _utc += duration; _timestamp += duration.Ticks; }
        public void MoveWallClock(TimeSpan duration) => _utc += duration;
    }
}
