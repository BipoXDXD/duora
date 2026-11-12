using Duora.Infrastructure.Participation;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Os jobs <c>expire-invite</c> e <c>game-deadline</c> que rodam depois de o estado da sala já ter mudado, ou para
/// um prazo que não é mais o gravado: o worker os conclui (<c>Done</c>) sem efeito algum sobre a sala nem sobre as
/// reservas. O relógio é falso e todos os jobs que não são o da sala ficam adiados, então o claim é
/// determinístico.
/// </summary>
public sealed class StaleRoomJobTests(PostgresFixture postgres)
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnExpireInviteJobThatRunsAfterBothAcceptedLeavesTheActiveRoomAlone()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        var inviteDeadline = await scene.InviteDeadlineAsync();
        await scene.AcceptBothAsync();
        var active = await scene.RoomSnapshotAsync();
        Assert.Equal(("Active", 2L), (active.State, active.ActiveReservations));
        scene.Clock.SetUtcNow(inviteDeadline);
        Assert.True(await scene.GameDeadlineAsync() > inviteDeadline);
        await scene.IsolateJobAsync("expire-invite");
        var job = await scene.ClaimAsync("expire-invite");

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(active, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobAsync("expire-invite"));
    }

    [Fact]
    public async Task AnExpireInviteJobForAnotherDeadlineLeavesThePastDueInvitationInvited()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        var inviteDeadline = await scene.InviteDeadlineAsync();
        await scene.ExecuteAsync(
            "update ops.jobs set due_at=$2,available_at=$2 where kind='expire-invite' and aggregate_id=$1",
            scene.Room,
            inviteDeadline - OneSecond);
        var invited = await scene.RoomSnapshotAsync();
        Assert.Equal(("Invited", 2L), (invited.State, invited.ActiveReservations));
        scene.Clock.SetUtcNow(inviteDeadline);
        await scene.IsolateJobAsync("expire-invite");
        var job = await scene.ClaimAsync("expire-invite");
        Assert.NotEqual(inviteDeadline, job.DueAt);

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(invited, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobAsync("expire-invite"));
    }

    [Fact]
    public async Task AGameDeadlineJobForAnotherDeadlineLeavesThePastDueActiveRoomActive()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        await scene.AcceptBothAsync();
        var deadline = await scene.GameDeadlineAsync();
        await scene.ExecuteAsync(
            "update ops.jobs set due_at=$2,available_at=$2 where kind='game-deadline' and aggregate_id=$1",
            scene.Room,
            deadline - OneSecond);
        var active = await scene.RoomSnapshotAsync();
        Assert.Equal(("Active", 2L), (active.State, active.ActiveReservations));
        scene.Clock.SetUtcNow(deadline);
        await scene.IsolateJobAsync("game-deadline");
        var job = await scene.ClaimAsync("game-deadline");
        Assert.NotEqual(deadline, job.DueAt);

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(active, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobAsync("game-deadline"));
    }

    [Fact]
    public async Task AGameDeadlineJobThatRunsAfterTheGameCompletedLeavesTheRoomCompleted()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        await scene.AcceptBothAsync();
        var deadline = await scene.GameDeadlineAsync();
        await scene.CompleteTheGameAsync();
        var completed = await scene.RoomSnapshotAsync();
        Assert.Equal("Completed", completed.State);
        scene.Clock.SetUtcNow(deadline);
        await scene.IsolateJobAsync("game-deadline");
        var job = await scene.ClaimAsync("game-deadline");
        Assert.Equal(deadline, job.DueAt);

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(completed, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobAsync("game-deadline"));
    }

    [Fact]
    public async Task AGameDeadlineJobThatRunsAfterAPlayerLeftLeavesTheRoomAborted()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        await scene.AcceptBothAsync();
        var deadline = await scene.GameDeadlineAsync();
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.ExitAsync(scene.Room, scene.Seed.Alice, Ct));
        var aborted = await scene.RoomSnapshotAsync();
        Assert.Equal(("Aborted", 0L), (aborted.State, aborted.ActiveReservations));
        scene.Clock.SetUtcNow(deadline);
        await scene.IsolateJobAsync("game-deadline");
        var job = await scene.ClaimAsync("game-deadline");

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(aborted, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobAsync("game-deadline"));
    }

    [Fact]
    public async Task ARepeatedGameDeadlineJobThatRunsAfterTheRoomExpiredLeavesItExpired()
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        await scene.AcceptBothAsync();
        var deadline = await scene.GameDeadlineAsync();
        scene.Clock.SetUtcNow(deadline);
        await scene.IsolateJobAsync("game-deadline");
        var first = await scene.ClaimAsync("game-deadline");
        Assert.True(await scene.Service.CompleteJobAsync(first, Ct));
        var expired = await scene.RoomSnapshotAsync();
        Assert.Equal(("Expired", 0L), (expired.State, expired.ActiveReservations));
        var repeatedId = Guid.NewGuid();
        await scene.ExecuteAsync(
            "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($2,'game-deadline',$1,$3,$3)",
            scene.Room,
            repeatedId,
            deadline + OneSecond);
        scene.Clock.SetUtcNow(deadline + OneSecond);
        await scene.IsolateJobAsync("game-deadline");
        var repeated = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(repeated);
        Assert.Equal(("game-deadline", scene.Room), (repeated.Kind, repeated.AggregateId));
        Assert.Equal(repeatedId, repeated.Id);

        Assert.True(await scene.Service.CompleteJobAsync(repeated, Ct));

        Assert.Equal(expired, await scene.RoomSnapshotAsync());
        Assert.Equal("Done|1", await scene.JobByIdAsync(repeated.Id));
        Assert.Equal("Done|1", await scene.JobByIdAsync(first.Id));
    }

    [Theory]
    [InlineData("expire-invite")]
    [InlineData("game-deadline")]
    public async Task ARoomJobForARoomWithoutParticipantsIsConcludedWithoutEffect(string kind)
    {
        await using var scene = await ParticipationScene.CreateAsync(postgres);
        await scene.InviteAliceAndBobAsync();
        var invited = await scene.RoomSnapshotAsync();
        var missing = Guid.NewGuid();
        var due = scene.Clock.GetUtcNow();
        await scene.ExecuteAsync(
            "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,$2,$3,$4,$4)",
            Guid.NewGuid(),
            kind,
            missing,
            due);
        await scene.ExecuteAsync(
            "update ops.jobs set available_at=$1+interval '10 years' where status='Pending' and aggregate_id<>$2",
            due,
            missing);
        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.Equal((kind, missing), (job.Kind, job.AggregateId));

        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));

        Assert.Equal(invited, await scene.RoomSnapshotAsync());
        await using var connection = await scene.Source.OpenConnectionAsync(Ct);
        await using var status = Command(connection, "select status from ops.jobs where id=$1", job.Id);
        Assert.Equal("Done", await status.ExecuteScalarAsync(Ct));
    }
}
