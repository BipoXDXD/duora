using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

public sealed class UnlikelyRoutesSnapshotTests
{
    [Fact]
    public void ReadsPublishedVersionOneFixtureWithUnknownFutureFields()
    {
        const string Json = """
            {
              "SnapshotVersion": 1,
              "PublicState": { "Stage": 1, "ChosenOptions": [], "StageDoneBy": [], "RevisionsUsed": 0, "Future": true },
              "PrivateStates": [
                { "Participant": "0199d700-0000-7000-8000-00000000000a", "ClueSetIndex": 1 },
                { "Participant": "0199d700-0000-7000-8000-00000000000b", "ClueSetIndex": 0 }
              ],
              "Control": {
                "RulesVersion": 1, "ContentVersion": 1, "SchemaVersion": 1,
                "First": "0199d700-0000-7000-8000-00000000000a",
                "Second": "0199d700-0000-7000-8000-00000000000b",
                "StartedAt": "2026-10-16T23:00:00Z", "Deadline": "2026-10-16T23:08:00Z",
                "Status": 1, "ResourceVersion": 9,
                "FirstChooser": "0199d700-0000-7000-8000-00000000000b", "MaxRevisions": 2
              },
              "Actions": []
            }
            """;
        var session = JsonSerializer.Deserialize<UnlikelyRoutesSnapshot>(Json)!.Restore(ContentV1());
        var engine = Engine(ClockAtRoundStart());
        session = AtChoices(engine, session);
        Assert.True(engine.ProjectForParticipant(session, Bia)!.IsMyTurn);
        Assert.Equal([BiaClueOne, BiaClueTwo], engine.ProjectForParticipant(session, Ana)!.MyClues);
        Assert.Equal(2, engine.ProjectForParticipant(session, Ana)!.RevisionsRemaining);
        Assert.Equal(new StateVersion(13), session.Version);
    }

    [Fact]
    public void RestoredSessionRetainsPrivateCluesTurnScheduleAndActionReceipts()
    {
        var engine = Engine(ClockAtRoundStart());
        var session = AtChoices(engine, NewSession(engine));
        var command = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(engine, session, command);
        var snapshot = UnlikelyRoutesSnapshot.Capture(session);
        var restored = JsonSerializer.Deserialize<UnlikelyRoutesSnapshot>(JsonSerializer.Serialize(snapshot))!.Restore(ContentV1());

        Assert.Equal(session.Schedule, restored.Schedule);
        Assert.Equal(session.Version, restored.Version);
        Assert.Equal([AnaClueOne, AnaClueTwo], engine.ProjectForParticipant(restored, Ana)!.MyClues);
        Assert.Equal([BiaClueOne, BiaClueTwo], engine.ProjectForParticipant(restored, Bia)!.MyClues);
        Assert.True(engine.ProjectForParticipant(restored, Bia)!.IsMyTurn);
        Assert.Single(engine.ProjectForParticipant(restored, Ana)!.Route);
        Assert.IsType<ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.AlreadyApplied>(engine.ValidateAction(restored, command));
        var altered = command with { Action = new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market") };
        Assert.Equal(RejectionReason.ActionIdReused,
            Assert.IsType<ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.Rejected>(engine.ValidateAction(restored, altered)).Reason);
        Assert.DoesNotContain(AnaClueOne, JsonSerializer.Serialize(snapshot.PublicState));
        Assert.DoesNotContain(BiaClueOne, JsonSerializer.Serialize(snapshot.Control));
    }

    [Fact]
    public void RestoredConfirmationAndRevisionLimitRemainEffective()
    {
        var engine = Engine(ClockAtRoundStart(), 0);
        var session = AtAdjustment(engine, NewSession(engine));
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        var restored = UnlikelyRoutesSnapshot.Capture(session).Restore(ContentV1());
        Assert.Equal(["confirm-route"], engine.ProjectForParticipant(restored, Bia)!.AvailableActions);
        restored = Act(engine, restored, Bia, new UnlikelyRoutesAction.ConfirmRoute());
        Assert.Equal(SessionStatus.Completed, restored.Status);
        Assert.Equal(RoutesStage.Conclusion, engine.ProjectForParticipant(UnlikelyRoutesSnapshot.Capture(restored).Restore(ContentV1()), Ana)!.Stage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeadlineRestoresPartialProgressWithoutExtendingSchedule(int progress)
    {
        var clock = ClockAtRoundStart();
        var engine = Engine(clock);
        var session = NewSession(engine);
        if (progress > 0)
        {
            session = AtChoices(engine, session);
        }
        if (progress > 1)
        {
            session = Act(engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        }
        clock.Advance(TimeSpan.FromMinutes(10));
        var expired = Assert.IsType<DeadlineOutcome<UnlikelyRoutesState, UnlikelyRoutesAction>.Resolved>(engine.HandleDeadline(session, session.Version)).Session;
        var restored = UnlikelyRoutesSnapshot.Capture(expired).Restore(ContentV1());
        Assert.Equal(SessionStatus.Expired, restored.Status);
        Assert.Equal(expired.Schedule.Deadline, restored.Schedule.Deadline);
        Assert.Empty(engine.ProjectForParticipant(restored, Ana)!.AvailableActions);
    }

    [Fact]
    public void RejectsActionLogLargerThanMaximumPossibleGame()
    {
        var engine = Engine(ClockAtRoundStart());
        var snapshot = UnlikelyRoutesSnapshot.Capture(AtChoices(engine, NewSession(engine)));
        var corrupt = snapshot with
        {
            Control = snapshot.Control with { ResourceVersion = 99 },
            Actions = Enumerable.Range(0, 17).Select(index => snapshot.Actions[0] with
            {
                ActionId = Guid.NewGuid(),
                ExpectedVersion = index + 1,
                ResultingVersion = index + 2,
            }).ToArray(),
        };
        Assert.Throws<ArgumentException>(() => corrupt.Restore(ContentV1()));
    }

    [Fact]
    public void RejectsCorruptSnapshotBoundaries()
    {
        var engine = Engine(ClockAtRoundStart());
        var snapshot = UnlikelyRoutesSnapshot.Capture(AtChoices(engine, NewSession(engine)));
        UnlikelyRoutesSnapshot[] invalid =
        [
            snapshot with { SnapshotVersion = 2 },
            snapshot with { Control = snapshot.Control with { ContentVersion = 2 } },
            snapshot with { Control = snapshot.Control with { FirstChooser = Outsider.Value } },
            snapshot with { Control = snapshot.Control with { Deadline = snapshot.Control.StartedAt } },
            snapshot with { PublicState = snapshot.PublicState with { ChosenOptions = [new("route-stop-1", "clock-garden")] } },
            snapshot with { PublicState = snapshot.PublicState with { ChosenOptions = [new("route-stop-2", "lighthouse")] } },
            snapshot with { PublicState = snapshot.PublicState with { StageDoneBy = [Ana.Value] } },
            snapshot with { PublicState = snapshot.PublicState with { RevisionsUsed = 2 } },
            snapshot with { PublicState = snapshot.PublicState with { Stage = RoutesStage.Conclusion } },
            snapshot with { PrivateStates = [snapshot.PrivateStates[0], snapshot.PrivateStates[0]] },
            snapshot with { PrivateStates = snapshot.PrivateStates.Select(item => item with { ClueSetIndex = 0 }).ToArray() },
            snapshot with { Actions = [snapshot.Actions[0] with { Actor = Outsider.Value }] },
            snapshot with { Actions = [snapshot.Actions[0] with { Sha256 = "invalid" }] },
            snapshot with { Actions = [snapshot.Actions[0] with { ResultingVersion = 99 }] },
            snapshot with { Actions = [snapshot.Actions[0], snapshot.Actions[0] with { ActionId = Guid.NewGuid() }] },
        ];
        foreach (var item in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => item.Restore(ContentV1()));
        }
    }
}
