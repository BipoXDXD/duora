using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Os prazos de API-R01 e API-E05 pelo HTTP e pelo PostgreSQL reais, com relógio falso.</summary>
public sealed class InviteAndCheckInDeadlineTests(PostgresFixture postgres)
{
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan CheckInWindow = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task AcceptIsRefusedFromTheExactInviteDeadlineAndReleasesBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(ct);
        var room = await InvitedRoomAsync(scene, ct);
        var deadline = await ReadInstantAsync(scene, "select invite_expires_at from experiences.game_sessions where id=$1", room, ct);

        scene.Clock.SetUtcNow(deadline - OneTick);
        using (var inTime = await AcceptAsync(scene.Alice, room))
        {
            Assert.Equal(HttpStatusCode.NoContent, inTime.StatusCode);
        }
        Assert.Equal(new RoomFacts("Invited", AliceAccepted: true, BobAccepted: false, ActiveReservations: 2, HasGame: false, GameDeadlineJobs: 0),
            await ReadRoomFactsAsync(scene, room, ct));

        scene.Clock.SetUtcNow(deadline);
        using (var late = await AcceptAsync(scene.Bob, room))
        {
            // Fixa o comportamento de hoje: o aceite tardio não aceita nada, mas responde 204; só a repetição recebe 404.
            Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        }
        Assert.Equal(new RoomFacts("Expired", AliceAccepted: true, BobAccepted: false, ActiveReservations: 0, HasGame: false, GameDeadlineJobs: 0),
            await ReadRoomFactsAsync(scene, room, ct));

        using var read = await scene.Bob.GetAsync($"/api/v1/rooms/{room}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct));
        Assert.Equal("Expired", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("game").ValueKind);

        using var repeated = await AcceptAsync(scene.Bob, room);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
    }

    [Fact]
    public async Task CheckInOpensExactlyTenMinutesBeforeTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(ct);
        var eventId = scene.Seed.Events[2];
        foreach (var user in new[] { scene.Seed.Alice, scene.Seed.Bob, scene.Seed.Third })
        {
            Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(eventId, user, false, ct));
        }
        var starts = await ReadInstantAsync(scene, "select starts_at from events.events where id=$1", eventId, ct);
        var opening = starts - CheckInWindow;

        scene.Clock.SetUtcNow(opening - OneTick);
        var aliceBefore = await ReadRegistrationFactsAsync(scene, eventId, scene.Seed.Alice, ct);
        Assert.Equal("Confirmed", aliceBefore.State);
        await AssertCheckInRefusedWithoutChangeAsync(scene, scene.Alice, eventId, scene.Seed.Alice, ct);

        scene.Clock.SetUtcNow(opening);
        await CheckInAsync(scene.Alice, eventId, HttpStatusCode.NoContent);
        Assert.Equal(aliceBefore with { State = "CheckedIn", CheckedInAt = opening, Version = aliceBefore.Version + 1 },
            await ReadRegistrationFactsAsync(scene, eventId, scene.Seed.Alice, ct));

        scene.Clock.SetUtcNow(starts - OneTick);
        await CheckInAsync(scene.Third, eventId, HttpStatusCode.NoContent);
        Assert.Equal("CheckedIn", (await ReadRegistrationFactsAsync(scene, eventId, scene.Seed.Third, ct)).State);

        scene.Clock.SetUtcNow(starts);
        await AssertCheckInRefusedWithoutChangeAsync(scene, scene.Bob, eventId, scene.Seed.Bob, ct);
    }

    private static async Task AssertCheckInRefusedWithoutChangeAsync(Scene scene, Browser browser, Guid eventId, Guid user, CancellationToken ct)
    {
        var before = await ReadRegistrationFactsAsync(scene, eventId, user, ct);
        await CheckInAsync(browser, eventId, HttpStatusCode.NotFound);
        Assert.Equal(before, await ReadRegistrationFactsAsync(scene, eventId, user, ct));
    }

    private static async Task CheckInAsync(Browser browser, Guid eventId, HttpStatusCode expected)
    {
        using var response = await browser.PostJsonAsync($"/api/v1/events/{eventId}/check-in", null);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<HttpResponseMessage> AcceptAsync(Browser browser, Guid room) =>
        browser.PostJsonAsync($"/api/v1/rooms/{room}/accept", null);

    private static async Task<Guid> InvitedRoomAsync(Scene scene, CancellationToken ct)
    {
        var eventId = scene.Seed.Events[0];
        foreach (var user in new[] { scene.Seed.Alice, scene.Seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(eventId, user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await scene.Service.RegisterAsync(eventId, user, true, ct));
        }
        scene.Clock.Advance(TimeSpan.FromMinutes(5));
        await scene.Service.BuildRoundPairingsAsync(scene.Seed.Rounds[0], ct);
        return (await scene.Service.StatusAsync(eventId, scene.Seed.Alice, ct))!.RoomId!.Value;
    }

    private static async Task<DateTimeOffset> ReadInstantAsync(Scene scene, string sql, Guid id, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, sql, id);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task<RoomFacts> ReadRoomFactsAsync(Scene scene, Guid room, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select s.state,
              (select gp.accepted_at is not null from experiences.game_participants gp where gp.game_session_id=s.id and gp.user_id=$2),
              (select gp.accepted_at is not null from experiences.game_participants gp where gp.game_session_id=s.id and gp.user_id=$3),
              (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
              s.public_state is not null or s.control_state is not null or s.game_started_at is not null or s.active_deadline_at is not null,
              (select count(*) from ops.jobs j where j.aggregate_id=s.id and j.kind='game-deadline')
            from experiences.game_sessions s where s.id=$1
            """, room, scene.Seed.Alice, scene.Seed.Bob);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return new(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3), reader.GetBoolean(4), reader.GetInt64(5));
    }

    private static async Task<RegistrationFacts> ReadRegistrationFactsAsync(Scene scene, Guid eventId, Guid user, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select r.state,r.checked_in_at,r.version,e.version,e.confirmed_count
            from events.registrations r join events.events e on e.id=r.event_id where r.event_id=$1 and r.user_id=$2
            """, eventId, user);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4));
    }

    private async Task<Scene> SceneAsync(CancellationToken ct)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var source = NpgsqlDataSource.Create(connectionString);
        var identity = new IdentityScenario(connectionString, factory);
        var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
        var third = await identity.SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));
        return new(factory, source, new ParticipationService(source, clock), seed, clock, alice, bob, third);
    }

    private sealed record RoomFacts(string State, bool AliceAccepted, bool BobAccepted, long ActiveReservations, bool HasGame, long GameDeadlineJobs);

    private sealed record RegistrationFacts(string State, DateTimeOffset? CheckedInAt, long Version, long EventVersion, int ConfirmedCount);

    private sealed record Scene(DuoraWebFactory Factory, NpgsqlDataSource Source, ParticipationService Service, ParticipationSeed Seed,
        FakeTimeProvider Clock, Browser Alice, Browser Bob, Browser Third) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            Bob.Dispose();
            Third.Dispose();
            await Factory.DisposeAsync();
            await Source.DisposeAsync();
        }
    }
}
