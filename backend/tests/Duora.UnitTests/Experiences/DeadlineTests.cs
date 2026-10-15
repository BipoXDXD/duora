using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;
using Outcome = Duora.Core.Experiences.DeadlineOutcome<
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesState,
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesAction>;
using Validation = Duora.Core.Experiences.ActionValidation<
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesState,
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesAction>;

namespace Duora.UnitTests.Experiences;

/// <summary>Prazo absoluto da sessão (spec 4.3) e <c>HandleDeadline</c> concorrente com ações (spec 10.4).</summary>
public sealed class DeadlineTests
{
    private static readonly DateTimeOffset Deadline = RoundStart.AddMinutes(8);

    private readonly ManualTimeProvider _clock = ClockAtRoundStart();
    private readonly UnlikelyRoutesEngine _engine;

    public DeadlineTests() => _engine = Engine(_clock);

    [Theory]
    [InlineData(0, 8 * 60)]
    [InlineData(59, (8 * 60) + 59)]
    [InlineData(60, 9 * 60)]
    [InlineData(61, 9 * 60)]
    [InlineData((8 * 60) + 59, 9 * 60)]
    public void DeadlineIsEightMinutesOfPlayCappedAtRoundStartPlusNine(int secondsAfterRoundStart, int expectedSeconds)
    {
        var schedule = GameSchedule.Plan(
            RoundStart, RoundStart.AddSeconds(secondsAfterRoundStart), GameTimeLimits.Default);

        Assert.Equal(RoundStart.AddSeconds(expectedSeconds), schedule.Deadline);
    }

    [Fact]
    public void CapAppliesFromTheFirstTickAfterOneMinute()
    {
        var gameStart = RoundStart.AddMinutes(1).AddTicks(1);

        var schedule = GameSchedule.Plan(RoundStart, gameStart, GameTimeLimits.Default);

        Assert.Equal(RoundStart.AddMinutes(9), schedule.Deadline);
    }

    [Theory]
    [InlineData(9 * 60)]
    [InlineData((9 * 60) + 1)]
    public void GameCannotStartWhenTheRoundLimitHasPassed(int secondsAfterRoundStart)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameSchedule.Plan(
            RoundStart, RoundStart.AddSeconds(secondsAfterRoundStart), GameTimeLimits.Default));
    }

    [Fact]
    public void GameCannotStartBeforeTheRound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GameSchedule.Plan(RoundStart, RoundStart.AddTicks(-1), GameTimeLimits.Default));
    }

    [Fact]
    public void CreateSessionUsesTheInjectedClockAsGameStart()
    {
        _clock.SetUtcNow(RoundStart.AddSeconds(90));

        var session = NewSession(_engine);

        Assert.Equal(RoundStart.AddSeconds(90), session.Schedule.StartedAt);
        Assert.Equal(RoundStart.AddMinutes(9), session.Schedule.Deadline);
        Assert.Equal(RoundStart.AddMinutes(9), _engine.ProjectForParticipant(session, Ana)!.Deadline);
    }

    [Fact]
    public void ActionOneTickBeforeTheDeadlineIsAccepted()
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline.AddTicks(-1));

        var validation = _engine.ValidateAction(session, Acknowledge(session));

        Assert.IsType<Validation.Accepted>(validation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60_000)]
    public void ActionAtOrAfterTheDeadlineIsRejected(int millisecondsAfterDeadline)
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline.AddMilliseconds(millisecondsAfterDeadline));

        var rejected = Assert.IsType<Validation.Rejected>(_engine.ValidateAction(session, Acknowledge(session)));

        Assert.Equal(RejectionReason.DeadlineReached, rejected.Reason);
    }

    [Fact]
    public void ResendOfAnAppliedActionAfterTheDeadlineStillReturnsTheReceipt()
    {
        var session = NewSession(_engine);
        var command = Acknowledge(session);
        session = Apply(_engine, session, command);
        _clock.SetUtcNow(Deadline);

        var resend = _engine.ValidateAction(session, command);

        Assert.Equal(new StateVersion(2), Assert.IsType<Validation.AlreadyApplied>(resend).Receipt.ResourceVersion);
    }

    [Fact]
    public void ResendAfterHandleDeadlineExpiredTheSessionReturnsTheOriginalReceipt()
    {
        var session = NewSession(_engine);
        var command = Acknowledge(session);
        session = Apply(_engine, session, command);
        _clock.SetUtcNow(Deadline);
        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version)).Session;

        var resend = _engine.ValidateAction(expired, command);

        Assert.Equal(SessionStatus.Expired, expired.Status);
        Assert.Equal(
            new ActionReceipt(command.ActionId, new StateVersion(2)),
            Assert.IsType<Validation.AlreadyApplied>(resend).Receipt);
    }

    [Fact]
    public void ResendOfTheFinalConfirmationOnACompletedSessionReturnsTheOriginalReceipt()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        var finalConfirmation = Command(session, Bia, new UnlikelyRoutesAction.ConfirmRoute());
        session = Apply(_engine, session, finalConfirmation);
        _clock.SetUtcNow(Deadline);

        var resend = _engine.ValidateAction(session, finalConfirmation);

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(
            new ActionReceipt(finalConfirmation.ActionId, new StateVersion(11)),
            Assert.IsType<Validation.AlreadyApplied>(resend).Receipt);
    }

    [Fact]
    public void HandleDeadlineOneTickBeforeTheDeadlineIsNotDue()
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline.AddTicks(-1));

        var outcome = _engine.HandleDeadline(session, session.Version);

        Assert.Equal(Deadline, Assert.IsType<Outcome.NotDue>(outcome).Deadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60_000)]
    public void HandleDeadlineAtOrAfterTheDeadlineExpiresAnUnfinishedRoute(int millisecondsAfterDeadline)
    {
        var session = AtChoices(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        _clock.SetUtcNow(Deadline.AddMilliseconds(millisecondsAfterDeadline));

        var resolved = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version));

        Assert.Equal(SessionStatus.Expired, resolved.Session.Status);
        Assert.Equal(session.Version.Next(), resolved.Session.Version);
        var view = _engine.ProjectForParticipant(resolved.Session, Bia)!;
        Assert.Equal(["Farol desativado"], view.Route.Select(stop => stop.OptionLabel));
        Assert.Empty(view.AvailableActions);
        var finished = Assert.IsType<SessionFinished>(Assert.Single(resolved.Events));
        Assert.Equal(new SessionFinished(SessionStatus.Expired, resolved.Session.Version, _clock.GetUtcNow()), finished);
    }

    [Fact]
    public void HandleDeadlineExpiresAWholeRouteWithoutBothConfirmations()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        _clock.SetUtcNow(Deadline);

        var resolved = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version));

        Assert.Equal(SessionStatus.Expired, resolved.Session.Status);
        var view = _engine.ProjectForParticipant(resolved.Session, Ana)!;
        Assert.Equal(RoutesStage.Adjustment, view.Stage);
        Assert.Equal(4, view.Route.Count);
        Assert.Equal(
            SessionStatus.Expired,
            Assert.IsType<SessionFinished>(Assert.Single(resolved.Events)).Outcome);
    }

    [Fact]
    public void HandleDeadlineIsDeterministicForTheSameInput()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        _clock.SetUtcNow(Deadline);

        var first = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version));
        var second = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version));

        Assert.Equal(first.Session.Status, second.Session.Status);
        Assert.Equal(first.Session.Version, second.Session.Version);
        Assert.Equal(first.Events, second.Events);
    }

    [Fact]
    public void HandleDeadlineWithStaleVersionIsAConflict()
    {
        var snapshot = NewSession(_engine);
        var afterAction = Act(_engine, snapshot, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        _clock.SetUtcNow(Deadline);

        var outcome = _engine.HandleDeadline(afterAction, snapshot.Version);

        Assert.Equal(new StateVersion(2), Assert.IsType<Outcome.VersionConflict>(outcome).CurrentVersion);
    }

    [Fact]
    public void ActionLosesToADeadlineAppliedFirstOnTheSameSnapshot()
    {
        var snapshot = NewSession(_engine);
        var command = Acknowledge(snapshot);
        _clock.SetUtcNow(Deadline);
        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(snapshot, snapshot.Version)).Session;

        var rejected = Assert.IsType<Validation.Rejected>(_engine.ValidateAction(expired, command));

        Assert.Equal(RejectionReason.SessionFinished, rejected.Reason);
    }

    [Fact]
    public void HandleDeadlineOnAFinishedSessionIsANoOp()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.ConfirmRoute());
        _clock.SetUtcNow(Deadline);

        var outcome = _engine.HandleDeadline(session, session.Version);

        Assert.Equal(SessionStatus.Completed, Assert.IsType<Outcome.AlreadyFinished>(outcome).Status);
    }

    [Fact]
    public void ExpiredSessionCannotExpireAgain()
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline);
        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version)).Session;

        var outcome = _engine.HandleDeadline(expired, expired.Version);

        Assert.Equal(SessionStatus.Expired, Assert.IsType<Outcome.AlreadyFinished>(outcome).Status);
    }

    private static GameCommand<UnlikelyRoutesAction> Acknowledge(UnlikelyRoutesSession session) =>
        Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
}
