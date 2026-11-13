using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duora.Contracts.Participation;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Quem não está na sala, pelo HTTP e pelo PostgreSQL reais (spec 8.2, TM-24).</summary>
public sealed partial class RoomAccessTests(PostgresFixture postgres)
{
    private const string Constellation = "constellation";
    private const string Routes = "routes";
    private const string Invited = "Invited";
    private const string Active = "Active";

    [Theory]
    [InlineData("read", Constellation, Active, HttpStatusCode.OK)]
    [InlineData("audio", Routes, Active, HttpStatusCode.OK)]
    [InlineData("accept", Constellation, Invited, HttpStatusCode.NoContent)]
    [InlineData("reject", Constellation, Invited, HttpStatusCode.NoContent)]
    [InlineData("exit", Constellation, Invited, HttpStatusCode.NoContent)]
    [InlineData("exit", Constellation, Active, HttpStatusCode.NoContent)]
    [InlineData("actions", Constellation, Active, HttpStatusCode.NoContent)]
    [InlineData("routes-actions", Routes, Active, HttpStatusCode.NoContent)]
    public async Task ForeignRoomRoutesAnswer404WithoutEffectOrForeignData(
        string route, string game, string state, HttpStatusCode participantStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(game, state, ct);
        var version = (await scene.Service.RoomAsync(scene.Room, scene.Seed.Alice, ct))!.Version;
        var before = await FingerprintAsync(scene, ct);

        using var foreign = await SendAsync(scene.Third, route, scene.Room, version);
        using var unknown = await SendAsync(scene.Third, route, Guid.NewGuid(), version);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var body = await foreign.Content.ReadAsStringAsync(ct);
        Assert.Equal(WithoutTraceId(await unknown.Content.ReadAsStringAsync(ct)), WithoutTraceId(body));
        foreach (var secret in ForeignTexts(scene))
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, await FingerprintAsync(scene, ct));

        // Sem este controle, um caminho errado também responderia 404 e o teste passaria sem provar nada.
        using var participant = await SendAsync(scene.Alice, route, scene.Room, version);
        Assert.Equal(participantStatus, participant.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParticipationStatusHidesTheRoomFromBothSidesAfterABlock(bool blockerIsTheLowUser)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(Constellation, Invited, ct);
        var low = await LowUserAsync(scene, ct);
        var aliceIsLow = low == scene.Seed.Alice;
        var (blocker, target) = aliceIsLow == blockerIsTheLowUser
            ? (scene.Alice, scene.Seed.Bob)
            : (scene.Bob, scene.Seed.Alice);
        var path = $"/api/v1/events/{scene.EventId}/participation";
        using (var invited = await scene.Alice.GetAsync(path))
        {
            Assert.Contains(scene.Room.ToString(), await invited.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        using var blocked = await blocker.PutAsync($"/api/v1/me/blocks/{target}");
        Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);

        foreach (var side in new[] { scene.Alice, scene.Bob })
        {
            using var status = await side.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var body = await status.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(["roomId", "roundCancelled", "state"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Waiting", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("roomId").ValueKind);
            Assert.DoesNotContain(scene.Room.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain("Unavailable", body, StringComparison.Ordinal);
        }
    }

    // O traceId muda a cada requisição; o resto do problem+json precisa ser igual ao de uma sala que não existe.
    private static string WithoutTraceId(string body) => TraceId().Replace(body, string.Empty);

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();

    private async Task<RoomScene> SceneAsync(string game, string state, CancellationToken ct)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        var eventId = game == Routes
            ? await RoutesBrowserScenarioSeed.InitializeAsync(connectionString, service, now, ct)
            : seed.Events[0];
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(await RoundAsync(source, eventId, ct), ct);
        var room = (await service.StatusAsync(eventId, seed.Alice, ct))!.RoomId!.Value;
        if (state == Active)
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, ct));
        }
        var identity = new IdentityScenario(connectionString, factory);
        var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
        var third = await identity.SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));
        return new(factory, source, service, seed, eventId, room, alice, bob, third);
    }

    private static async Task<Guid> RoundAsync(NpgsqlDataSource source, Guid eventId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select id from events.rounds where event_id=$1", eventId);
        return (Guid)(await query.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Guid> LowUserAsync(RoomScene scene, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select p.user_low from events.pairings p join experiences.game_sessions s on s.pairing_id=p.id where s.id=$1
            """, scene.Room);
        return (Guid)(await query.ExecuteScalarAsync(ct))!;
    }

    private static Task<HttpResponseMessage> SendAsync(Browser browser, string route, Guid room, long version)
    {
        var path = $"/api/v1/rooms/{room}";
        var expected = version.ToString(CultureInfo.InvariantCulture);
        return route switch
        {
            "read" => browser.GetAsync(path),
            "audio" => browser.GetAsync($"{path}/audio/route-intro-en"),
            "accept" or "reject" or "exit" => browser.PostJsonAsync($"{path}/{route}", null),
            "actions" => browser.PostJsonAsync($"{path}/actions", new RoomActionRequest(Guid.NewGuid(), expected, "choose-option", "morning", "tea")),
            "routes-actions" => browser.PostJsonAsync($"{path}/routes-actions", new RoutesActionRequest(Guid.NewGuid(), expected, "acknowledge", "Introduction", null, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Rota sem envio definido."),
        };
    }

    private static string[] ForeignTexts(RoomScene scene) =>
    [
        scene.Room.ToString(), scene.Seed.Alice.ToString(), scene.Seed.Bob.ToString(), "alice@example.com", "bob@example.com",
        "Shared choices", "morning", "Imaginary route", "route-intro", Invited, Active,
    ];

    /// <summary>Tudo o que uma chamada indevida poderia mudar na sala, nas reservas ou nos jobs dela.</summary>
    private static async Task<string> FingerprintAsync(RoomScene scene, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select concat_ws('|', s.state, s.version, md5(coalesce(s.public_state::text,'')), md5(coalesce(s.control_state::text,'')),
              (select string_agg(concat_ws(',', gp.user_id, gp.accepted_at, gp.departed_at, md5(coalesce(gp.private_state::text,''))), ';' order by gp.user_id)
                 from experiences.game_participants gp where gp.game_session_id=s.id),
              (select count(*) from experiences.game_actions a where a.game_session_id=s.id),
              (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
              (select count(*) from events.pairing_participants pp where pp.user_id=$2),
              (select count(*) from ops.jobs j where j.aggregate_id=s.id))
            from experiences.game_sessions s where s.id=$1
            """, scene.Room, scene.Seed.Third);
        return (string)(await query.ExecuteScalarAsync(ct))!;
    }

    private sealed record RoomScene(DuoraWebFactory Factory, NpgsqlDataSource Source, ParticipationService Service, ParticipationSeed Seed,
        Guid EventId, Guid Room, Browser Alice, Browser Bob, Browser Third) : IAsyncDisposable
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
