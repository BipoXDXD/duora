using System.Globalization;
using System.Net;
using Duora.Contracts.Participation;
using Duora.Core.Experiences.UnlikelyRoutes;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class RoutesParticipationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task BilingualCluesTurnsReplayRevisionAndCompletionSurviveRestart()
    {
        var (source, clock, service, seed, room, _) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        var alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        var bob = (await service.RoomAsync(room, seed.Bob, ct))!;
        Assert.Null(alice.Game);
        Assert.Equal("Imaginary route", alice.RoutesGame!.Title);
        Assert.Equal("Rota imaginária", bob.RoutesGame!.Title);
        Assert.Empty(alice.RoutesGame.MyClues);
        Assert.Single(alice.Audio);
        Assert.NotNull(await service.AudioAsync(room, seed.Alice, "route-intro-en", ct));
        Assert.Null(await service.AudioAsync(room, seed.Third, "route-intro-en", ct));
        await Acknowledge(service, seed, room, "Introduction", ct);
        alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        bob = (await service.RoomAsync(room, seed.Bob, ct))!;
        Assert.Single(alice.RoutesGame!.MyClues);
        Assert.Single(bob.RoutesGame!.MyClues);
        Assert.NotEqual(alice.RoutesGame.MyClues[0].Contains("park", StringComparison.Ordinal),
            bob.RoutesGame.MyClues[0].Contains("parque", StringComparison.Ordinal));
        var deadline = alice.RoutesGame.Deadline;
        await Acknowledge(service, seed, room, "Clues", ct);
        alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        var chooser = alice.RoutesGame!.IsMyTurn ? seed.Alice : seed.Bob;
        var partner = chooser == seed.Alice ? seed.Bob : seed.Alice;
        var id = Guid.NewGuid();
        Assert.Equal(ParticipationOutcome.Invalid, await service.RoutesActionAsync(room, partner, Guid.NewGuid(), alice.Version, "choose-option", null, "stop", "park", ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RoutesActionAsync(room, chooser, id, alice.Version, "choose-option", null, "stop", "park", ct));
        service = new ParticipationService(source, clock);
        Assert.Equal(ParticipationOutcome.Done, await service.RoutesActionAsync(room, chooser, id, alice.Version, "choose-option", null, "stop", "park", ct));
        Assert.Equal(ParticipationOutcome.Invalid, await service.RoutesActionAsync(room, chooser, id, alice.Version, "choose-option", null, "stop", "lake", ct));
        bob = (await service.RoomAsync(room, partner, ct))!;
        Assert.True(bob.RoutesGame!.IsMyTurn);
        Assert.Equal(deadline, bob.RoutesGame.Deadline);
        Assert.Single(bob.RoutesGame.Route);
        await Action(service, room, partner, "choose-option", null, "second", "lake", ct);
        await Action(service, room, chooser, "confirm-route", null, null, null, ct);
        await Action(service, room, partner, "request-revision", null, null, null, ct);
        service = new ParticipationService(source, clock);
        alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        Assert.Empty(alice.RoutesGame!.Route);
        Assert.Equal(0, alice.RoutesGame.RevisionsRemaining);
        await Action(service, room, chooser, "choose-option", null, "stop", "lake", ct);
        await Action(service, room, partner, "choose-option", null, "second", "park", ct);
        alice = (await service.RoomAsync(room, seed.Alice, ct))!;
        Assert.Equal(ParticipationOutcome.Invalid, await service.RoutesActionAsync(room, seed.Alice, Guid.NewGuid(), alice.Version, "request-revision", null, null, null, ct));
        await Action(service, room, seed.Alice, "confirm-route", null, null, null, ct);
        await Action(service, room, seed.Bob, "confirm-route", null, null, null, ct);
        var final = (await new ParticipationService(source, clock).RoomAsync(room, seed.Alice, ct))!;
        Assert.Equal("Completed", final.State);
        Assert.Equal(RoutesStage.Conclusion, final.RoutesGame!.Stage);
        Assert.Equal(2, final.RoutesGame.Route.Count);
        Assert.Empty(final.RoutesGame.AvailableActions);
        Assert.Equal(0L, await Reservations(source, seed, ct));
    }

    [Fact]
    public async Task HttpValidatesPayloadAndVersionReplaysOnceAndBlockAbortsRoom()
    {
        var (source, clock, service, seed, room, connectionString) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var identity = new IdentityScenario(connectionString, factory);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        var request = new RoutesActionRequest(Guid.NewGuid(), before.Version.ToString(CultureInfo.InvariantCulture), "acknowledge", "Introduction", null, null);
        var endpoint = $"/api/v1/rooms/{room}/routes-actions";
        using (var malformed = await alice.PostRawJsonAsync(endpoint, "{"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        foreach (var invalid in new[]
        {
            request with { ExpectedVersion = "0" },
            request with { ExpectedVersion = "9223372036854775808" },
            request with { ExpectedVersion = "1.0" },
            request with { ActionId = Guid.Empty },
            request with { Type = "skip-card" },
            request with { Stage = "Conclusion" },
            request with { OptionId = "park" },
        })
        {
            using var rejected = await alice.PostJsonAsync(endpoint, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using (var accepted = await alice.PostJsonAsync(endpoint, request))
        using (var replay = await alice.PostJsonAsync(endpoint, request))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        }
        Assert.Equal(before.Version + 1, (await service.RoomAsync(room, seed.Alice, ct))!.Version);
        using (var conflict = await alice.PostJsonAsync(endpoint, request with { ActionId = Guid.NewGuid() }))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        using (var changedBody = await alice.PostJsonAsync(endpoint, request with { Stage = "Clues" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, changedBody.StatusCode);
        }
        using (var block = await alice.PutAsync($"/api/v1/me/blocks/{seed.Bob}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);
        }
        using (var read = await alice.GetAsync($"/api/v1/rooms/{room}"))
        using (var replay = await alice.PostJsonAsync(endpoint, request))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        }
        Assert.Equal(0L, await Reservations(source, seed, ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select state from experiences.game_sessions where id=$1", room);
        Assert.Equal("Aborted", await query.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task RecoveryUsesAuthoritativeScheduleStatusAndVersionColumns()
    {
        var (source, clock, service, seed, room, _) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            await Execute(connection, """
                update experiences.game_sessions
                set control_state=control_state || '{"resourceVersion":999,"status":2,"startedAt":"2000-01-01T00:00:00Z","deadline":"2000-01-01T00:08:00Z"}'::jsonb
                where id=$1
                """, ct, room);
        }
        var restored = (await new ParticipationService(source, clock).RoomAsync(room, seed.Alice, ct))!;
        Assert.Equal(before.Version, restored.Version);
        Assert.Equal(before.Version, restored.RoutesGame!.ResourceVersion);
        Assert.Equal(before.RoutesGame!.Deadline, restored.RoutesGame.Deadline);
        Assert.Equal(before.RoutesGame.Status, restored.RoutesGame.Status);
        await Action(service, room, seed.Alice, "acknowledge", "Introduction", null, null, ct);
    }

    [Fact]
    public async Task ConcurrentAcknowledgementsConflictAndRetryAdvancesOnce()
    {
        var (source, clock, service, seed, room, _) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        var outcomes = await Task.WhenAll(
            service.RoutesActionAsync(room, seed.Alice, Guid.NewGuid(), before.Version, "acknowledge", "Introduction", null, null, ct),
            service.RoutesActionAsync(room, seed.Bob, Guid.NewGuid(), before.Version, "acknowledge", "Introduction", null, null, ct));
        Assert.Single(outcomes, outcome => outcome == ParticipationOutcome.Done);
        Assert.Single(outcomes, outcome => outcome == ParticipationOutcome.Conflict);
        var retryUser = outcomes[0] == ParticipationOutcome.Conflict ? seed.Alice : seed.Bob;
        await Action(service, room, retryUser, "acknowledge", "Introduction", null, null, ct);
        var after = (await new ParticipationService(source, clock).RoomAsync(room, seed.Alice, ct))!;
        Assert.Equal(before.Version + 2, after.Version);
        Assert.Equal(RoutesStage.Clues, after.RoutesGame!.Stage);
    }

    [Fact]
    public async Task DeadlineRecoveryExpiresPartialRoutesEvenAfterEligibilityIsLost()
    {
        var (source, clock, service, seed, room, _) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await Acknowledge(service, seed, room, "Introduction", ct);
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await Execute(c, "update identity.users set status='Restricted' where id=$1", ct, seed.Bob);
        }
        clock.Advance(TimeSpan.FromMinutes(9));
        service = new ParticipationService(source, clock);
        for (var i = 0; i < 30; i++)
        {
            var job = await service.ClaimJobAsync(ct);
            if (job is null)
            {
                break;
            }
            await service.CompleteJobAsync(job, ct);
        }
        Assert.Equal(0L, await Reservations(source, seed, ct));
        Assert.Null(await service.RoomAsync(room, seed.Alice, ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select state from experiences.game_sessions where id=$1", room);
        Assert.Equal("Expired", await query.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongExperienceOutsiderAndPostCommitGuardsRejectActions(bool block)
    {
        var (source, _, service, seed, room, _) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        Assert.Equal(ParticipationOutcome.Unavailable, await service.ActionAsync(room, seed.Alice, Guid.NewGuid(), before.Version, "skip-card", "morning", null, ct));
        Assert.Equal(ParticipationOutcome.Unavailable, await service.RoutesActionAsync(room, seed.Third, Guid.NewGuid(), before.Version, "acknowledge", "Introduction", null, null, ct));
        Assert.Null(await service.RoomAsync(room, seed.Third, ct));
        await using var c = await source.OpenConnectionAsync(ct);
        if (block)
        {
            await Execute(c, "insert into safety.blocks(id,blocker_id,blocked_id,created_at) values($1,$2,$3,$4)", ct, Guid.NewGuid(), seed.Bob, seed.Alice, DateTimeOffset.UtcNow);
        }
        else
        {
            await Execute(c, "update identity.users set status='Restricted' where id=$1", ct, seed.Bob);
        }
        Assert.Null(await service.RoomAsync(room, seed.Alice, ct));
        Assert.Null(await service.AudioAsync(room, seed.Alice, "route-intro-en", ct));
        Assert.Equal(ParticipationOutcome.Unavailable, await service.RoutesActionAsync(room, seed.Alice, Guid.NewGuid(), before.Version, "acknowledge", "Introduction", null, null, ct));
        await using var query = Command(c, "select version from experiences.game_sessions where id=$1", room);
        Assert.Equal(before.Version, await query.ExecuteScalarAsync(ct));
    }

    private async Task<(NpgsqlDataSource Source, FakeTimeProvider Clock, ParticipationService Service, ParticipationSeed Seed, Guid Room, string ConnectionString)> Active()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var source = NpgsqlDataSource.Create(connectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, "delete from identity.user_roles where user_id=$1", ct, seed.Third);
        await Execute(connection, "insert into identity.user_roles(user_id,role_id) select $1,id from identity.roles where name='product_admin'", ct, seed.Third);
        await Execute(connection, "update identity.users set preferred_locale='pt-BR' where id=$1", ct, seed.Bob);
        var request = RoutesCatalogPublicationTests.Request();
        var contentId = await service.PublishRoutesCatalogAsync(seed.Third, Content(request.EnUS), Content(request.PtBR),
            request.Audio.Select(cue => new CatalogAudioUpload(cue.CueId, Variant(cue.EnUS), Variant(cue.PtBR))).ToArray(), true, ct);
        Assert.NotNull(contentId);
        await Execute(connection, "update events.events set experience_version_id=$2 where id=$1", ct, seed.Events[0], contentId.Value);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, ct));
        return (source, clock, service, seed, room, connectionString);
    }

    private static UnlikelyRoutesContent Content(RoutesCatalogContentRequest input) => new(1, input.Title, input.Objective, input.Example,
        new[] { input.Stops[0], input.Stops[0] with { StepId = "second" } }
            .Select(stop => new RouteStop(stop.StepId, stop.Prompt, stop.Options.Select(option => new RouteOption(option.OptionId, option.Label)).ToArray())).ToArray(),
        input.ClueSets.Select(set => (IReadOnlyList<Clue>)set.Select(clue => new Clue(clue.Text)).ToArray()).ToArray());
    private static CatalogAudioUploadVariant Variant(CatalogAudioVariantRequest input) =>
        new(input.AssetId, input.Transcript, input.AccessibleDescription, input.Provenance, input.Sha256, input.Bytes);

    private static async Task Acknowledge(ParticipationService service, ParticipationSeed seed, Guid room, string stage, CancellationToken ct)
    {
        await Action(service, room, seed.Alice, "acknowledge", stage, null, null, ct);
        await Action(service, room, seed.Bob, "acknowledge", stage, null, null, ct);
    }

    private static async Task Action(ParticipationService service, Guid room, Guid user, string type, string? stage, string? step, string? option, CancellationToken ct)
    {
        var current = (await service.RoomAsync(room, user, ct))!;
        Assert.Equal(ParticipationOutcome.Done, await service.RoutesActionAsync(room, user, Guid.NewGuid(), current.Version, type, stage, step, option, ct));
    }

    private static async Task<long> Reservations(NpgsqlDataSource source, ParticipationSeed seed, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", new[] { seed.Alice, seed.Bob });
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }
}
