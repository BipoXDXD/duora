using Duora.Core.Events;
using static Duora.UnitTests.Events.EventStates;
using static Duora.UnitTests.Events.RoundStates;

namespace Duora.UnitTests.Events;

/// <summary>Retomada depois de reinício (spec 5.2, INV-08): a rodada passada nunca recomeça como nova.</summary>
public sealed class RoundResumeTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    [Fact]
    public void ScheduledRoundBeforeItsStartKeepsWaiting()
    {
        var round = Scheduled();

        var outcome = round.Resume(1, RoundStartsAt - Tick);

        AssertResumedUnchanged(round, outcome, RoundState.Scheduled, 1);
    }

    [Theory]
    [InlineData(RoundState.Scheduled)]
    [InlineData(RoundState.Preparing)]
    [InlineData(RoundState.Inviting)]
    public void RoundThatHasNotRunYetResumesJustBeforeTheStartDeadline(RoundState state)
    {
        var round = In(state);
        var version = round.Version;

        var outcome = round.Resume(version, StartDeadline - Tick);

        AssertResumedUnchanged(round, outcome, state, version);
    }

    [Theory]
    [InlineData(RoundState.Scheduled)]
    [InlineData(RoundState.Preparing)]
    [InlineData(RoundState.Inviting)]
    public void RoundThatHasNotRunYetIsCancelledFromTheStartDeadline(RoundState state)
    {
        var round = In(state);
        var version = round.Version;

        var outcome = round.Resume(version, StartDeadline);

        Assert.Equal(ReleaseAndNotice(StartDeadline), outcome.Effects);
        Assert.Equal((RoundState.Cancelled, version + 1), (round.State, round.Version));
    }

    [Fact]
    public void RunningRoundResumesJustBeforeTheWindowEnds()
    {
        var round = Running();

        var outcome = round.Resume(4, RoundEndsAt - Tick);

        AssertResumedUnchanged(round, outcome, RoundState.Running, 4);
    }

    [Fact]
    public void RunningRoundIsClosedFromTheEndOfTheWindow()
    {
        var round = Running();
        var now = RoundEndsAt.AddMinutes(30);

        var outcome = round.Resume(4, now);

        Assert.Equal(ReleaseAndNotice(now), outcome.Effects);
        Assert.Equal((RoundState.Closed, 5L), (round.State, round.Version));
    }

    [Theory]
    [InlineData(RoundState.Closed)]
    [InlineData(RoundState.Cancelled)]
    public void FinishedRoundNeverRestartsEvenInsideItsWindow(RoundState state)
    {
        var round = In(state);
        var version = round.Version;

        var outcome = round.Resume(version, OnTime);

        AssertResumedUnchanged(round, outcome, state, version);
    }

    [Fact]
    public void RepeatingTheResumeThatCancelledDoesNotRepeatItsEffects()
    {
        var round = Preparing();
        round.Resume(2, StartDeadline);

        var repeated = round.Resume(2, StartDeadline.AddMinutes(1));

        AssertResumedUnchanged(round, repeated, RoundState.Cancelled, 3);
    }

    [Fact]
    public void ResumeWithAnotherVersionIsAConflict()
    {
        var round = Inviting();

        AssertRefused(RoundRefusal.VersionConflict, round.Resume(2, StartDeadline));
        Assert.Equal((RoundState.Inviting, 3L), (round.State, round.Version));
    }

    private static void AssertResumedUnchanged(
        Round round,
        Outcome<RoundRefusal> outcome,
        RoundState expectedState,
        long expectedVersion)
    {
        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal((expectedState, expectedVersion), (round.State, round.Version));
    }
}
