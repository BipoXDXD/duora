using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class ParticipationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task TwoCoordinatorsReserveOnceAndRestartPreservesPrivateLocalizedGame()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        await Join(service, seed.Events[0], seed.Alice, ct);
        await Join(service, seed.Events[0], seed.Bob, ct);
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await Execute(c, "update identity.users set preferred_locale='pt-BR' where id=$1", ct, seed.Bob);
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.WhenAll(service.BuildRoundPairingsAsync(seed.Rounds[0], ct), service.BuildRoundPairingsAsync(seed.Rounds[0], ct));
        var status = await service.StatusAsync(seed.Events[0], seed.Alice, ct);
        var room = Assert.IsType<Guid>(status!.RoomId);
        Assert.Null(await service.RoomAsync(room, seed.Third, ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, ct));
        Assert.Null((await service.RoomAsync(room, seed.Alice, ct))!.Game);
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, ct));
        var alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        var bob = (await service.RoomAsync(room, seed.Bob, ct))!;
        Assert.Equal("en", alice.Language);
        Assert.Equal(alice.Game!.CurrentCard!.CardId, bob.Game!.CurrentCard!.CardId);
        Assert.NotEqual(alice.Game.Title, bob.Game.Title);
        var actionId = Guid.NewGuid();
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Alice, actionId, alice.Version, "choose-option", "morning", "tea", ct));
        // A fresh service rehydrates the persisted state; the partner's answer remains private.
        service = new ParticipationService(source, clock);
        bob = (await service.RoomAsync(room, seed.Bob, ct))!;
        Assert.Null(bob.Game!.MyAnswer);
        Assert.Empty(bob.Game.Revealed);
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Alice, actionId, alice.Version, "choose-option", "morning", "tea", ct));
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Bob, Guid.NewGuid(), bob.Version, "choose-option", "morning", "walk", ct));
        Assert.Equal("Completed", (await service.RoomAsync(room, seed.Bob, ct))!.State);
        Assert.Equal(0, await ActiveReservations(source, seed.Alice, seed.Bob, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherParticipantRejectsAndReleasesBoth(bool second)
    {
        var (source, clock, service, seed, room) = await Invited();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, second ? seed.Bob : seed.Alice, false, ct));
        Assert.Equal("Aborted", (await service.RoomAsync(room, seed.Alice, ct))!.State);
        Assert.Equal(0, await ActiveReservations(source, seed.Alice, seed.Bob, ct));
        Assert.Equal(ParticipationOutcome.Unavailable, await service.RespondAsync(room, seed.Bob, true, ct));
    }

    [Fact]
    public async Task ExpiredLeaseFencesOldWorkerAndInviteExpiryReleasesBoth()
    {
        var (source, clock, service, seed, room) = await Invited();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        // Use an isolated job selection: this fixture's invite, leaving other parallel tests untouched.
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await Execute(c, "update ops.jobs set available_at=$2 where aggregate_id=$1 and kind='expire-invite'", ct, room, clock.GetUtcNow().AddYears(-1));
        }

        clock.Advance(TimeSpan.FromSeconds(61));
        var first = await ClaimSpecific(source, service, room, ct);
        clock.Advance(TimeSpan.FromSeconds(61));
        var second = await ClaimSpecific(source, service, room, ct);
        Assert.True(second.Token > first.Token);
        Assert.True(await service.CompleteJobAsync(second, ct));
        Assert.False(await service.CompleteJobAsync(first, ct));
        Assert.Equal("Expired", (await service.RoomAsync(room, seed.Alice, ct))!.State);
        Assert.Equal(0, await ActiveReservations(source, seed.Alice, seed.Bob, ct));
    }

    [Fact]
    public async Task GameDeadlineRecoversWithSubMicrosecondClockAndIneligibleParticipant()
    {
        var (source, clock, service, seed, room) = await Invited();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        clock.Advance(TimeSpan.FromTicks(7));
        await service.RespondAsync(room, seed.Alice, true, ct);
        await service.RespondAsync(room, seed.Bob, true, ct);
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await Execute(c, "update identity.users set status='Restricted' where id=$1", ct, seed.Bob);
        }
        clock.Advance(TimeSpan.FromMinutes(9));
        for (var i = 0; i < 20; i++)
        {
            var job = await service.ClaimJobAsync(ct);
            if (job is null)
            {
                break;
            }
            await service.CompleteJobAsync(job, ct);
        }
        Assert.Equal(0, await ActiveReservations(source, seed.Alice, seed.Bob, ct));
        Assert.Null(await service.RoomAsync(room, seed.Alice, ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select state from experiences.game_sessions where id=$1", room);
        // The existing engine marks a timed-out card as Passed and the session Expired.
        Assert.Equal("Expired", await query.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task PausedMatchingKeepsRoundJobRecoverable()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connection, now, ct, declarePreferences: true);
        await using var source = NpgsqlDataSource.Create(connection);
        var clock = new FakeTimeProvider(now.AddMinutes(5));
        var paused = new ParticipationService(source, clock,
            Microsoft.Extensions.Options.Options.Create(new ParticipationOptions { MatchingEnabled = false }));
        Assert.Null(await paused.ClaimJobAsync(ct));
        var resumed = new ParticipationService(source, clock);
        Assert.Equal(seed.Rounds[0], (await resumed.ClaimJobAsync(ct))!.AggregateId);
    }

    private async Task<(NpgsqlDataSource Source, FakeTimeProvider Clock, ParticipationService Service, ParticipationSeed Seed, Guid Room)> Invited()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connection, now, ct, declarePreferences: true);
        var source = NpgsqlDataSource.Create(connection);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        await Join(service, seed.Events[0], seed.Alice, ct);
        await Join(service, seed.Events[0], seed.Bob, ct);
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        return (source, clock, service, seed, room);
    }

    private static async Task Join(ParticipationService service, Guid eventId, Guid user, CancellationToken ct)
    {
        Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, ct));
    }

    private static async Task<long> ActiveReservations(NpgsqlDataSource source, Guid first, Guid second, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Command(c, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", new[] { first, second });
        return (long)(await q.ExecuteScalarAsync(ct))!;
    }

    private static async Task<ParticipationJob> ClaimSpecific(NpgsqlDataSource source, ParticipationService service, Guid room, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var job = await service.ClaimJobAsync(ct);
            Assert.NotNull(job);
            if (job.AggregateId == room)
            {
                return job;
            }

            await service.CompleteJobAsync(job, ct);
        }
        throw new InvalidOperationException("Invite job missing.");
    }
}
