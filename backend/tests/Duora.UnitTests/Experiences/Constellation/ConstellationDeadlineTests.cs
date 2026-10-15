using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;
using Outcome = Duora.Core.Experiences.DeadlineOutcome<
    Duora.Core.Experiences.Constellation.ConstellationState,
    Duora.Core.Experiences.Constellation.ConstellationAction>;
using Validation = Duora.Core.Experiences.ActionValidation<
    Duora.Core.Experiences.Constellation.ConstellationState,
    Duora.Core.Experiences.Constellation.ConstellationAction>;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>Prazo absoluto (spec 4.3) e saída neutra por timeout (spec 6.3), no intervalo semiaberto [início, prazo).</summary>
public sealed class ConstellationDeadlineTests
{
    private readonly ManualTimeProvider _clock = ClockAtRoundStart();
    private readonly ConstellationEngine _engine;

    public ConstellationDeadlineTests() => _engine = Engine(_clock);

    [Fact]
    public void AnswerOneTickBeforeTheDeadlineIsAccepted()
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline.AddTicks(-1));

        var validation = _engine.ValidateAction(session, Command(session, Ana, Choose(SundayCard, "picnic")));

        Assert.IsType<Validation.Accepted>(validation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60_000)]
    public void AnswerAtOrAfterTheDeadlineIsRejected(int millisecondsAfterDeadline)
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline.AddMilliseconds(millisecondsAfterDeadline));

        var validation = _engine.ValidateAction(session, Command(session, Ana, Skip(SundayCard)));

        Assert.Equal(RejectionReason.DeadlineReached, Assert.IsType<Validation.Rejected>(validation).Reason);
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
    public void DeadlinePassesTheOpenCardNeutrallyAndKeepsWhatWasRevealed(int millisecondsAfterDeadline)
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "corn-cake"));
        _clock.SetUtcNow(Deadline.AddMilliseconds(millisecondsAfterDeadline));

        var resolved = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version));

        Assert.Equal(SessionStatus.Expired, resolved.Session.Status);
        Assert.Equal(session.Version.Next(), resolved.Session.Version);
        Assert.Equal(
            new SessionFinished(SessionStatus.Expired, resolved.Session.Version, _clock.GetUtcNow()),
            Assert.IsType<SessionFinished>(Assert.Single(resolved.Events)));
        foreach (var viewer in new[] { Ana, Bia })
        {
            var view = _engine.ProjectForParticipant(resolved.Session, viewer)!;
            Assert.Equal(ConstellationStage.Summary, view.Stage);
            Assert.Equal([SundayCard], view.Revealed.Select(card => card.CardId));
            Assert.Equal([PowerCard, IceCreamCard], view.Passed.Select(card => card.CardId));
            Assert.Null(view.CurrentCard);
            Assert.Null(view.MyAnswer);
            Assert.False(view.AwaitingPartner);
            Assert.Empty(view.AvailableActions);
        }
    }

    [Fact]
    public void DeadlineWithoutAnyAnswerRevealsNothingAndInventsNoAnswer()
    {
        var session = NewSession(_engine);
        _clock.SetUtcNow(Deadline);

        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version)).Session;

        var view = _engine.ProjectForParticipant(expired, Bia)!;
        Assert.Empty(view.Revealed);
        Assert.Equal([new PassedCardView(SundayCard, "Para um domingo sem planos, qual destas?")], view.Passed);
    }

    [Fact]
    public void ResendAfterTheSessionExpiredReturnsTheOriginalReceipt()
    {
        var session = NewSession(_engine);
        var command = Command(session, Ana, Choose(SundayCard, "picnic"));
        session = Apply(_engine, session, command);
        _clock.SetUtcNow(Deadline);
        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version)).Session;

        var resend = _engine.ValidateAction(expired, command);

        Assert.Equal(
            new ActionReceipt(command.ActionId, new StateVersion(2)),
            Assert.IsType<Validation.AlreadyApplied>(resend).Receipt);
    }

    [Fact]
    public void HandleDeadlineWithStaleVersionIsAConflict()
    {
        var snapshot = NewSession(_engine);
        var afterAnswer = Act(_engine, snapshot, Ana, Skip(SundayCard));
        _clock.SetUtcNow(Deadline);

        var outcome = _engine.HandleDeadline(afterAnswer, snapshot.Version);

        Assert.Equal(new StateVersion(2), Assert.IsType<Outcome.VersionConflict>(outcome).CurrentVersion);
    }

    [Fact]
    public void HandleDeadlineOnACompletedSessionIsANoOp()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Skip(IceCreamCard));
        session = Act(_engine, session, Bia, Skip(IceCreamCard));
        _clock.SetUtcNow(Deadline);

        var outcome = _engine.HandleDeadline(session, session.Version);

        Assert.Equal(SessionStatus.Completed, Assert.IsType<Outcome.AlreadyFinished>(outcome).Status);
    }
}
