using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;
using static Duora.UnitTests.Events.EventStates;
using static Duora.UnitTests.Events.RoundStates;

namespace Duora.UnitTests.Events;

public sealed class RoundTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public enum Operation
    {
        BeginPreparation,
        BeginInviting,
        BeginRunning,
        Close,
        Cancel,
    }

    [Fact]
    public void ScheduledRoundTakesItsTimesFromTheEventSchedule()
    {
        var round = Scheduled();

        Assert.Equal(RoundId, round.Id);
        Assert.Equal(EventId, round.EventId);
        Assert.Equal(1, round.Number);
        Assert.Equal(RoundState.Scheduled, round.State);
        Assert.Equal(1, round.Version);
        Assert.Equal(RoundStartsAt, round.StartsAt);
        Assert.Equal(StartDeadline, round.StartDeadline);
        Assert.Equal(RoundEndsAt, round.EndsAt);
    }

    [Fact]
    public void LaterRoundStartsOneWindowAfterThePrevious()
    {
        var round = Round.Schedule(RoundId, EventId, number: 3, Schedule);

        Assert.Equal(StartsAt.AddMinutes(24), round.StartsAt);
        Assert.Equal(StartsAt.AddMinutes(25), round.StartDeadline);
        Assert.Equal(StartsAt.AddMinutes(36), round.EndsAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ScheduleRejectsNumberOutsideTheSnapshot(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Round.Schedule(RoundId, EventId, number, Schedule));
    }

    [Fact]
    public void ScheduleRejectsEmptyIds()
    {
        Assert.Throws<ArgumentException>(() => Round.Schedule(Guid.Empty, EventId, 1, Schedule));
        Assert.Throws<ArgumentException>(() => Round.Schedule(RoundId, Guid.Empty, 1, Schedule));
    }

    [Fact]
    public void HappyPathAdvancesOneVersionPerTransition()
    {
        var round = Scheduled();

        AssertAccepted(round.BeginPreparation(1, OnTime));
        Assert.Equal((RoundState.Preparing, 2L), (round.State, round.Version));
        AssertAccepted(round.BeginInviting(2, OnTime));
        Assert.Equal((RoundState.Inviting, 3L), (round.State, round.Version));
        AssertAccepted(round.BeginRunning(3, OnTime));
        Assert.Equal((RoundState.Running, 4L), (round.State, round.Version));
        var closing = round.Close(4, RoundEndsAt);
        Assert.Equal((RoundState.Closed, 5L), (round.State, round.Version));
        Assert.Equal([new ReservationsReleaseRequested(EventId, RoundId, RoundEndsAt)], closing.Effects);
    }

    [Fact]
    public void StartTransitionsHaveNoEffects()
    {
        var round = Scheduled();

        Assert.Empty(round.BeginPreparation(1, OnTime).Effects);
        Assert.Empty(round.BeginInviting(2, OnTime).Effects);
        Assert.Empty(round.BeginRunning(3, OnTime).Effects);
    }

    [Fact]
    public void PreparationIsRefusedJustBeforeTheRoundStarts()
    {
        var round = Scheduled();

        AssertRefused(RoundRefusal.NotDueYet, round.BeginPreparation(1, RoundStartsAt - Tick));
        Assert.Equal((RoundState.Scheduled, 1L), (round.State, round.Version));
    }

    [Fact]
    public void PreparationIsAcceptedAtTheStartInstant()
    {
        AssertAccepted(Scheduled().BeginPreparation(1, RoundStartsAt));
    }

    [Fact]
    public void PreparationIsAcceptedJustBeforeTheStartDeadline()
    {
        AssertAccepted(Scheduled().BeginPreparation(1, StartDeadline - Tick));
    }

    [Fact]
    public void PreparationIsRefusedFromTheStartDeadline()
    {
        var round = Scheduled();

        AssertRefused(RoundRefusal.StartWindowElapsed, round.BeginPreparation(1, StartDeadline));
        Assert.Equal(RoundState.Scheduled, round.State);
    }

    [Fact]
    public void InvitingIsAcceptedJustBeforeTheStartDeadline()
    {
        var round = Preparing();

        AssertAccepted(round.BeginInviting(round.Version, StartDeadline - Tick));
    }

    [Fact]
    public void InvitingIsRefusedFromTheStartDeadline()
    {
        var round = Preparing();

        AssertRefused(RoundRefusal.StartWindowElapsed, round.BeginInviting(round.Version, StartDeadline));
        Assert.Equal(RoundState.Preparing, round.State);
    }

    [Fact]
    public void RunningIsAcceptedJustBeforeTheStartDeadline()
    {
        var round = Inviting();

        AssertAccepted(round.BeginRunning(round.Version, StartDeadline - Tick));
    }

    [Fact]
    public void RunningIsRefusedFromTheStartDeadlineBecauseTheGameNoLongerFits()
    {
        var round = Inviting();

        AssertRefused(RoundRefusal.StartWindowElapsed, round.BeginRunning(round.Version, StartDeadline));
        Assert.Equal((RoundState.Inviting, 3L), (round.State, round.Version));
    }

    /// <summary>
    /// O worker sem reinício (<c>BeginRunning</c>) e o que acabou de reiniciar (<c>Resume</c>) aplicam a mesma regra
    /// de início (spec 4.3: o jogo precisa caber até início + 9 min) no mesmo instante.
    /// </summary>
    [Fact]
    public void BeginRunningAndResumeAgreeJustBeforeTheStartDeadline()
    {
        var now = StartDeadline - Tick;
        var running = Inviting();
        var resumed = Inviting();

        AssertAccepted(running.BeginRunning(running.Version, now));
        AssertAccepted(resumed.Resume(resumed.Version, now));

        Assert.Equal(RoundState.Running, running.State);
        Assert.Equal(RoundState.Inviting, resumed.State);
    }

    [Fact]
    public void BeginRunningAndResumeAgreeAtTheStartDeadline()
    {
        var running = Inviting();
        var resumed = Inviting();

        AssertRefused(RoundRefusal.StartWindowElapsed, running.BeginRunning(running.Version, StartDeadline));
        AssertAccepted(resumed.Resume(resumed.Version, StartDeadline));

        Assert.Equal(RoundState.Inviting, running.State);
        Assert.Equal(RoundState.Cancelled, resumed.State);
    }

    [Fact]
    public void CloseIsRefusedJustBeforeTheWindowEnds()
    {
        var round = Running();

        AssertRefused(RoundRefusal.NotDueYet, round.Close(round.Version, RoundEndsAt - Tick));
        Assert.Equal(RoundState.Running, round.State);
    }

    [Theory]
    [InlineData(RoundState.Scheduled)]
    [InlineData(RoundState.Preparing)]
    [InlineData(RoundState.Inviting)]
    [InlineData(RoundState.Running)]
    public void CancelReleasesReservationsAndSendsTheNeutralNotice(RoundState state)
    {
        var round = In(state);
        var version = round.Version;

        var outcome = round.Cancel(version, OnTime);

        Assert.Equal(ReleaseAndNotice(OnTime), outcome.Effects);
        Assert.Equal((RoundState.Cancelled, version + 1), (round.State, round.Version));
    }

    [Theory]
    [MemberData(nameof(ForbiddenTransitions))]
    public void ForbiddenTransitionIsRefusedAndKeepsStateAndVersion(RoundState state, Operation operation)
    {
        var round = In(state);
        var version = round.Version;

        var outcome = Apply(round, operation, version);

        AssertRefused(RoundRefusal.InvalidState, outcome);
        Assert.Equal((state, version), (round.State, round.Version));
    }

    public static TheoryData<RoundState, Operation> ForbiddenTransitions()
    {
        var allowed = new HashSet<(RoundState, Operation)>
        {
            (RoundState.Scheduled, Operation.BeginPreparation),
            (RoundState.Preparing, Operation.BeginInviting),
            (RoundState.Inviting, Operation.BeginRunning),
            (RoundState.Running, Operation.Close),
            (RoundState.Scheduled, Operation.Cancel),
            (RoundState.Preparing, Operation.Cancel),
            (RoundState.Inviting, Operation.Cancel),
            (RoundState.Running, Operation.Cancel),
        };

        var data = new TheoryData<RoundState, Operation>();
        foreach (var state in Enum.GetValues<RoundState>())
        {
            foreach (var operation in Enum.GetValues<Operation>().Where(op => !allowed.Contains((state, op))))
            {
                data.Add(state, operation);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TransitionWithAnotherVersionIsAConflict(long expectedVersion)
    {
        var round = Scheduled();

        AssertRefused(RoundRefusal.VersionConflict, round.BeginPreparation(expectedVersion, OnTime));
        Assert.Equal((RoundState.Scheduled, 1L), (round.State, round.Version));
    }

    [Fact]
    public void StaleVersionForADifferentTransitionIsAConflict()
    {
        var round = Preparing();

        AssertRefused(RoundRefusal.VersionConflict, round.Cancel(1, OnTime));
        Assert.Equal((RoundState.Preparing, 2L), (round.State, round.Version));
    }

    [Fact]
    public void RepeatingATransitionWithTheSameVersionIsAcceptedWithoutChange()
    {
        var round = Scheduled();
        round.BeginPreparation(1, OnTime);

        var repeated = round.BeginPreparation(1, OnTime);

        AssertAccepted(repeated);
        Assert.Empty(repeated.Effects);
        Assert.Equal((RoundState.Preparing, 2L), (round.State, round.Version));
    }

    [Fact]
    public void RepeatingCloseDoesNotRepeatItsEffects()
    {
        var round = Running();
        round.Close(4, RoundEndsAt);

        var repeated = round.Close(4, RoundEndsAt.AddMinutes(1));

        AssertAccepted(repeated);
        Assert.Empty(repeated.Effects);
        Assert.Equal((RoundState.Closed, 5L), (round.State, round.Version));
    }

    [Fact]
    public void RepeatingCancelDoesNotRepeatItsEffects()
    {
        var round = Inviting();
        round.Cancel(3, OnTime);

        var repeated = round.Cancel(3, OnTime);

        AssertAccepted(repeated);
        Assert.Empty(repeated.Effects);
        Assert.Equal((RoundState.Cancelled, 4L), (round.State, round.Version));
    }

    /// <summary>Cada operação num instante em que a guarda de horário passaria, para isolar a guarda de estado.</summary>
    private static Outcome<RoundRefusal> Apply(Round round, Operation operation, long version) => operation switch
    {
        Operation.BeginPreparation => round.BeginPreparation(version, OnTime),
        Operation.BeginInviting => round.BeginInviting(version, OnTime),
        Operation.BeginRunning => round.BeginRunning(version, OnTime),
        Operation.Close => round.Close(version, RoundEndsAt),
        Operation.Cancel => round.Cancel(version, OnTime),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Operação sem caso de teste."),
    };
}
