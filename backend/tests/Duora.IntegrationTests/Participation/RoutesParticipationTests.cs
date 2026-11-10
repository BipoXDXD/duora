using System.Globalization;
using System.Net;
using System.Text.Json;
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

    /// <summary>
    /// Como em API-R03: entre o prazo e o job a sala segue <c>Active</c> no banco, mas uma ação nova recebe, antes e depois
    /// do job, a mesma <c>404</c> de uma sala inexistente, em status, corpo sem <c>traceId</c> e headers, sem gravar nada.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task AnActionAtOrAfterTheDeadlineGetsTheSame404BeforeAndAfterTheDeadlineJob(int secondsAfterDeadline)
    {
        var (source, clock, service, seed, room, connectionString) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        using var alice = await new IdentityScenario(connectionString, factory)
            .SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        await using var connection = await source.OpenConnectionAsync(ct);
        var active = await RoomFactsAsync(connection, room, ct);
        Assert.Equal(new RoomFacts("Active", before.Version, ActiveReservations: 2, Actions: 0), active);
        clock.SetUtcNow((await DeadlineAsync(connection, room, ct)).AddSeconds(secondsAfterDeadline));
        var endpoint = $"/api/v1/rooms/{room}/routes-actions";

        using var late = await alice.PostJsonAsync(endpoint, AcknowledgeIntroduction(before.Version));

        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(late, ct));
        var unknownRoom = await UnknownRoomAsync(alice, before.Version, ct);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(late, ct));
        Assert.Equal(active, await RoomFactsAsync(connection, room, ct));

        await RunDueJobsAsync(service, ct);
        var expired = await RoomFactsAsync(connection, room, ct);
        Assert.Equal(new RoomFacts("Expired", before.Version + 1, ActiveReservations: 0, Actions: 0), expired);

        using var afterTheJob = await alice.PostJsonAsync(endpoint, AcknowledgeIntroduction(before.Version));
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(afterTheJob, ct));
        Assert.Equal(expired, await RoomFactsAsync(connection, room, ct));
    }

    /// <summary>
    /// Uma ação nova com <c>expectedVersion</c> desatualizado, no prazo e antes do job, recebe a mesma <c>404</c> da ação
    /// atrasada e de uma sala inexistente, não o <c>409</c> de conflito. Nada é gravado.
    /// </summary>
    [Fact]
    public async Task ANewActionWithAStaleVersionAtTheDeadlineGetsTheSame404()
    {
        var (source, clock, service, seed, room, connectionString) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var identity = new IdentityScenario(connectionString, factory);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        using var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        await using var connection = await source.OpenConnectionAsync(ct);
        var deadline = await DeadlineAsync(connection, room, ct);
        var endpoint = $"/api/v1/rooms/{room}/routes-actions";
        clock.SetUtcNow(deadline.AddTicks(-1));
        using (var applied = await alice.PostJsonAsync(endpoint, AcknowledgeIntroduction(before.Version)))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await RoomFactsAsync(connection, room, ct);
        Assert.Equal(new RoomFacts("Active", before.Version + 1, ActiveReservations: 2, Actions: 1), afterTheAction);
        clock.SetUtcNow(deadline);

        using var stale = await bob.PostJsonAsync(endpoint, AcknowledgeIntroduction(before.Version));

        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(stale, ct));
        Assert.Equal(await UnknownRoomAsync(bob, before.Version, ct), await NeutralResponse.DescribeAsync(stale, ct));
        Assert.Equal(afterTheAction, await RoomFactsAsync(connection, room, ct));
    }

    /// <summary>
    /// Resíduo aceito pela coordenação em 10/11, como em API-R03: até o job, uma ação aplicada um tick antes do prazo e
    /// reenviada no prazo responde <c>204</c> sem gravar de novo, e o mesmo <c>actionId</c> com outro corpo responde
    /// <c>400</c>. Depois do job, os dois recebem <c>404</c>.
    /// </summary>
    [Fact]
    public async Task AnActionAppliedInTimeIsReplayedAtTheDeadlineUntilTheJobRuns()
    {
        var (source, clock, service, seed, room, connectionString) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        using var alice = await new IdentityScenario(connectionString, factory)
            .SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var before = (await service.RoomAsync(room, seed.Alice, ct))!;
        await using var connection = await source.OpenConnectionAsync(ct);
        var deadline = await DeadlineAsync(connection, room, ct);
        var endpoint = $"/api/v1/rooms/{room}/routes-actions";
        var acknowledged = AcknowledgeIntroduction(before.Version);
        clock.SetUtcNow(deadline.AddTicks(-1));
        using (var applied = await alice.PostJsonAsync(endpoint, acknowledged))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await RoomFactsAsync(connection, room, ct);
        Assert.Equal(new RoomFacts("Active", before.Version + 1, ActiveReservations: 2, Actions: 1), afterTheAction);
        clock.SetUtcNow(deadline);

        using (var replay = await alice.PostJsonAsync(endpoint, acknowledged))
        using (var reused = await alice.PostJsonAsync(endpoint, acknowledged with { Stage = "Clues" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused, ct));
        }

        Assert.Equal(afterTheAction, await RoomFactsAsync(connection, room, ct));

        await RunDueJobsAsync(service, ct);
        var expired = await RoomFactsAsync(connection, room, ct);
        Assert.Equal(new RoomFacts("Expired", before.Version + 2, ActiveReservations: 0, Actions: 1), expired);

        var unknownRoom = await UnknownRoomAsync(alice, before.Version, ct);
        using (var replay = await alice.PostJsonAsync(endpoint, acknowledged))
        using (var reused = await alice.PostJsonAsync(endpoint, acknowledged with { Stage = "Clues" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
            Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(replay, ct));
            Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(reused, ct));
        }

        Assert.Equal(expired, await RoomFactsAsync(connection, room, ct));
    }

    /// <summary>
    /// O prazo só vale para a ação bem formada. Um payload malformado no prazo é recusado antes de a sala ser lida: a
    /// resposta é igual à do mesmo payload numa sala inexistente, e nada é gravado. O handler recusa a versão inválida com
    /// <c>400 VALIDATION_FAILED</c>; JSON inválido e GUID malformado falham no model binding com <c>400 INVALID_REQUEST</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"actionId":"6f1c2a4e-0000-4000-8000-000000000002","expectedVersion":"0","type":"acknowledge","stage":"Introduction"}""", "VALIDATION_FAILED")]
    [InlineData("""{"actionId":"not-a-guid","expectedVersion":"1","type":"acknowledge","stage":"Introduction"}""", "INVALID_REQUEST")]
    [InlineData("""{"actionId":""", "INVALID_REQUEST")]
    public async Task AMalformedActionAtTheDeadlineIsRejectedBeforeTheRoomIsRead(string json, string code)
    {
        var (source, clock, _, seed, room, connectionString) = await Active();
        await using var owned = source;
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        using var alice = await new IdentityScenario(connectionString, factory)
            .SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        await using var connection = await source.OpenConnectionAsync(ct);
        var active = await RoomFactsAsync(connection, room, ct);
        clock.SetUtcNow(await DeadlineAsync(connection, room, ct));

        using var malformed = await alice.PostRawJsonAsync($"/api/v1/rooms/{room}/routes-actions", json);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(malformed, ct));
        using var unknown = await alice.PostRawJsonAsync($"/api/v1/rooms/{Guid.NewGuid()}/routes-actions", json);
        Assert.Equal(await NeutralResponse.DescribeAsync(unknown, ct), await NeutralResponse.DescribeAsync(malformed, ct));
        Assert.Equal(active, await RoomFactsAsync(connection, room, ct));
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

    private static RoutesActionRequest AcknowledgeIntroduction(long version) =>
        new(Guid.NewGuid(), version.ToString(CultureInfo.InvariantCulture), "acknowledge", "Introduction", null, null);

    private static async Task<DateTimeOffset> DeadlineAsync(NpgsqlConnection connection, Guid room, CancellationToken ct)
    {
        await using var query = Command(connection, "select active_deadline_at from experiences.game_sessions where id=$1", room);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>Estado e versão da sala, reservas ainda ativas do par e ações gravadas: tudo o que uma ação poderia mudar.</summary>
    private static async Task<RoomFacts> RoomFactsAsync(NpgsqlConnection connection, Guid room, CancellationToken ct)
    {
        await using var query = Command(connection, """
            select s.state,s.version,
              (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
              (select count(*) from experiences.game_actions a where a.game_session_id=s.id)
            from experiences.game_sessions s where s.id=$1
            """, room);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task RunDueJobsAsync(ParticipationService service, CancellationToken ct)
    {
        for (var job = await service.ClaimJobAsync(ct); job is not null; job = await service.ClaimJobAsync(ct))
        {
            await service.CompleteJobAsync(job, ct);
        }
    }

    /// <summary>A mesma ação numa sala que não existe: a resposta que a sala vencida não pode distinguir.</summary>
    private static async Task<string> UnknownRoomAsync(Browser browser, long version, CancellationToken ct)
    {
        using var unknown = await browser.PostJsonAsync($"/api/v1/rooms/{Guid.NewGuid()}/routes-actions", AcknowledgeIntroduction(version));
        return await NeutralResponse.DescribeAsync(unknown, ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static async Task<long> Reservations(NpgsqlDataSource source, ParticipationSeed seed, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", new[] { seed.Alice, seed.Bob });
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }

    private sealed record RoomFacts(string State, long Version, long ActiveReservations, long Actions);
}
