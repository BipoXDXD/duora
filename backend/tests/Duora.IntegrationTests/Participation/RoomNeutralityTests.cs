using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duora.Contracts.Participation;
using Duora.Core.Safety;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Sala abortada não diz por quê (TM-24, spec 7.3): para quem fica, saída, suspensão e bloqueio são a mesma resposta.</summary>
public sealed partial class RoomNeutralityTests(PostgresFixture postgres)
{
    private const string Constellation = "constellation";
    private const string Routes = "routes";
    private const string Invited = "Invited";
    private const string Active = "Active";
    private const string WaitingWithoutRoom = """{"state":"Waiting","roomId":null,"roundCancelled":false}""";
    private static readonly string[] NotFoundRoutes = ["read", "audio", "accept", "reject", "actions", "routes-actions"];

    [Theory]
    [InlineData(Constellation, Invited)]
    [InlineData(Constellation, Active)]
    [InlineData(Routes, Active)]
    public async Task PartnerExitSuspensionAndBlockLookTheSameOnEveryRoomRouteAndStatus(string game, string state)
    {
        var ct = TestContext.Current.CancellationToken;

        var afterExit = await ObservePartnerEndingAsync("exit", game, state, ct);
        var afterSuspension = await ObservePartnerEndingAsync("suspension", game, state, ct);
        var afterBlock = await ObservePartnerEndingAsync("block", game, state, ct);

        Assert.Equal(afterExit, afterSuspension);
        Assert.Equal(afterExit, afterBlock);
        Assert.All(NotFoundRoutes, route => Assert.StartsWith("404|", afterExit[route], StringComparison.Ordinal));
        Assert.Equal(afterExit["unknown-room"], afterExit["read"]);
        Assert.StartsWith($"200|application/json; charset=utf-8|{WaitingWithoutRoom}|", afterExit["status"], StringComparison.Ordinal);
        Assert.StartsWith("204|", afterExit["exit"], StringComparison.Ordinal);
        Assert.Equal(afterExit["status"], afterExit["status-after-exit"]);
    }

    [Fact]
    public async Task AnInvitationRejectedByThePartnerLooksLikeAnyOtherAbortedRoom()
    {
        var ct = TestContext.Current.CancellationToken;

        var afterRejection = await ObservePartnerEndingAsync("reject", Constellation, Invited, ct);
        var afterBlock = await ObservePartnerEndingAsync("block", Constellation, Invited, ct);

        Assert.Equal(afterBlock, afterRejection);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("suspension")]
    [InlineData("block")]
    public async Task AnAbortedRoomInALaterRoundDoesNotBringBackTheEarlierRoom(string how)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SecondRoundSceneAsync(ct);
        var statusPath = $"/api/v1/events/{scene.EventId}/participation";
        using (var invited = await scene.Bob.GetAsync(statusPath))
        {
            Assert.Contains(scene.Room.ToString(), await invited.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        await EndByPartnerAsync(scene, how, ct);

        using var status = await scene.Bob.GetAsync(statusPath);
        Assert.Equal(WaitingWithoutRoom, await status.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task WhoeverLeftSeesAMissingRoomAndCanRepeatTheExitWithoutNewEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(Constellation, Active, ct);
        using (var left = await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/exit", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }
        var version = await SessionVersionAsync(scene, ct);

        using var read = await scene.Alice.GetAsync($"/api/v1/rooms/{scene.Room}");
        using var unknown = await scene.Alice.GetAsync($"/api/v1/rooms/{Guid.NewGuid()}");
        using var status = await scene.Alice.GetAsync($"/api/v1/events/{scene.EventId}/participation");
        using var repeated = await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/exit", null);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(await DescribeAsync(unknown, ct), await DescribeAsync(read, ct));
        Assert.Equal(WaitingWithoutRoom, await status.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(version, await SessionVersionAsync(scene, ct));
        Assert.Equal(0L, await ActiveReservationsAsync(scene, ct));
    }

    [Fact]
    public async Task LeavingARoomKeepsBothPeopleAvailableForTheNextEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(Constellation, Active, ct);
        using (var left = await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/exit", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }
        var nextEvent = scene.Seed.Events[1];
        foreach (var browser in new[] { scene.Alice, scene.Bob })
        {
            using var registered = await browser.PutAsync($"/api/v1/events/{nextEvent}/registration");
            Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
            using var checkedIn = await browser.PostJsonAsync($"/api/v1/events/{nextEvent}/check-in", null);
            Assert.Equal(HttpStatusCode.NoContent, checkedIn.StatusCode);
        }

        scene.Clock.Advance(TimeSpan.FromMinutes(5));
        await scene.Service.BuildRoundPairingsAsync(scene.Seed.Rounds[1], ct);

        using var status = await scene.Bob.GetAsync($"/api/v1/events/{nextEvent}/participation");
        var body = await status.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(Invited, json.RootElement.GetProperty("state").GetString());
        Assert.NotEqual(scene.Room, json.RootElement.GetProperty("roomId").GetGuid());
    }

    [Fact]
    public async Task ACompletedRoomStaysReadableAndKeepsItsRoomInTheStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(Constellation, Active, ct);
        var version = (await scene.Service.RoomAsync(scene.Room, scene.Seed.Alice, ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.ActionAsync(scene.Room, scene.Seed.Alice, Guid.NewGuid(), version, "choose-option", "morning", "tea", ct));
        version = (await scene.Service.RoomAsync(scene.Room, scene.Seed.Bob, ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await scene.Service.ActionAsync(scene.Room, scene.Seed.Bob, Guid.NewGuid(), version, "choose-option", "morning", "walk", ct));

        await AssertRoomStillVisibleAsync(scene, "Completed", "Completed", ct);
    }

    [Fact]
    public async Task AnExpiredInvitationStaysReadableAndKeepsItsRoomInTheStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scene = await SceneAsync(Constellation, Invited, ct);
        scene.Clock.Advance(TimeSpan.FromSeconds(60));
        using (var late = await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/accept", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        }

        await AssertRoomStillVisibleAsync(scene, "Expired", "Unavailable", ct);
    }

    private static async Task AssertRoomStillVisibleAsync(RoomScene scene, string roomState, string participationState, CancellationToken ct)
    {
        using var read = await scene.Bob.GetAsync($"/api/v1/rooms/{scene.Room}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var room = JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct));
        Assert.Equal(roomState, room.RootElement.GetProperty("state").GetString());
        using var status = await scene.Bob.GetAsync($"/api/v1/events/{scene.EventId}/participation");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
        Assert.Equal(participationState, json.RootElement.GetProperty("state").GetString());
        Assert.Equal(scene.Room, json.RootElement.GetProperty("roomId").GetGuid());
    }

    private async Task<IReadOnlyDictionary<string, string>> ObservePartnerEndingAsync(string how, string game, string state, CancellationToken ct)
    {
        await using var scene = await SceneAsync(game, state, ct);
        var version = (await scene.Service.RoomAsync(scene.Room, scene.Seed.Alice, ct))!.Version;
        await EndByPartnerAsync(scene, how, ct);

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in NotFoundRoutes)
        {
            using var response = await SendAsync(scene.Bob, route, scene.Room, version);
            seen[route] = await DescribeAsync(response, ct);
        }
        using (var unknown = await SendAsync(scene.Bob, "read", Guid.NewGuid(), version))
        {
            seen["unknown-room"] = await DescribeAsync(unknown, ct);
        }
        var statusPath = $"/api/v1/events/{scene.EventId}/participation";
        using (var status = await scene.Bob.GetAsync(statusPath))
        {
            seen["status"] = await DescribeAsync(status, ct);
        }
        using (var exit = await scene.Bob.PostJsonAsync($"/api/v1/rooms/{scene.Room}/exit", null))
        {
            seen["exit"] = await DescribeAsync(exit, ct);
        }
        using (var status = await scene.Bob.GetAsync(statusPath))
        {
            seen["status-after-exit"] = await DescribeAsync(status, ct);
        }
        return seen;
    }

    private static async Task EndByPartnerAsync(RoomScene scene, string how, CancellationToken ct)
    {
        if (how == "suspension")
        {
            await using var services = scene.Factory.Services.CreateAsyncScope();
            var outcome = await services.ServiceProvider.GetRequiredService<AccountSuspensions>().SuspendAutomaticallyAsync(
                SuspensionOrder.Automated(scene.Seed.Alice, SuspensionReason.SelfieVerificationFailed, "Limite de tentativas."),
                ModerationCommandKey.ForSystem("selfie-verification", Guid.NewGuid()),
                ct);
            Assert.IsType<SuspensionOutcome.Suspended>(outcome);
            return;
        }
        using var ended = how switch
        {
            "exit" => await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/exit", null),
            "reject" => await scene.Alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/reject", null),
            "block" => await scene.Alice.PutAsync($"/api/v1/me/blocks/{scene.Seed.Bob}"),
            _ => throw new ArgumentOutOfRangeException(nameof(how), how, "Encerramento sem envio definido."),
        };
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(Browser browser, string route, Guid room, long version)
    {
        var path = $"/api/v1/rooms/{room}";
        var expected = version.ToString(CultureInfo.InvariantCulture);
        return route switch
        {
            "read" => browser.GetAsync(path),
            "audio" => browser.GetAsync($"{path}/audio/route-intro-en"),
            "accept" or "reject" => browser.PostJsonAsync($"{path}/{route}", null),
            "actions" => browser.PostJsonAsync($"{path}/actions", new RoomActionRequest(Guid.NewGuid(), expected, "choose-option", "morning", "tea")),
            "routes-actions" => browser.PostJsonAsync($"{path}/routes-actions", new RoutesActionRequest(Guid.NewGuid(), expected, "acknowledge", "Introduction", null, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Rota sem envio definido."),
        };
    }

    // Só o traceId muda de uma requisição para outra; qualquer outra diferença seria um sinal do motivo.
    // Date e Set-Cookie variam em qualquer resposta e não dependem da sala.
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(ct), string.Empty);
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Set-Cookie" or "Content-Type"))
            .Select(header => $"{header.Key}={string.Join(',', header.Value)}")
            .Order(StringComparer.Ordinal);
        return $"{(int)response.StatusCode}|{response.Content.Headers.ContentType}|{body}|{string.Join(';', headers)}";
    }

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
        return new(factory, source, clock, service, seed, eventId, room, alice, bob);
    }

    /// <summary>A rodada 1 termina Completed e a rodada 2 fica em Invited; Alice do cenário é quem encerra a sala e Bob, quem jogou as duas.</summary>
    private async Task<RoomScene> SecondRoundSceneAsync(CancellationToken ct)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        var eventId = seed.Events[0];
        var secondRound = Guid.NewGuid();
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,2,'Scheduled',$3,$4,42)", ct,
                secondRound, eventId, now.AddMinutes(17), now.AddMinutes(29));
        }
        Guid[] everyone = [seed.Alice, seed.Bob, seed.Third];
        foreach (var user in everyone)
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        List<Guid> paired = [];
        foreach (var user in everyone)
        {
            if ((await service.StatusAsync(eventId, user, ct))!.RoomId is not null)
            {
                paired.Add(user);
            }
        }
        Assert.Equal(2, paired.Count);
        var firstRoom = (await service.StatusAsync(eventId, paired[0], ct))!.RoomId!.Value;
        await PlayToCompletionAsync(service, firstRoom, paired[0], paired[1], ct);

        clock.Advance(TimeSpan.FromMinutes(12));
        await service.BuildRoundPairingsAsync(secondRound, ct);
        var ending = everyone.Except(paired).Single();
        var room = (await service.StatusAsync(eventId, ending, ct))!.RoomId!.Value;
        Guid? watcher = null;
        foreach (var user in paired)
        {
            if ((await service.StatusAsync(eventId, user, ct))!.RoomId == room)
            {
                watcher = user;
            }
        }
        Assert.NotNull(watcher);
        var emails = new Dictionary<Guid, string> { [seed.Alice] = "alice@example.com", [seed.Bob] = "bob@example.com", [seed.Third] = "third@example.com" };
        var identity = new IdentityScenario(connectionString, factory);
        var endingBrowser = await identity.SignedInBrowserAsync(new(ending, emails[ending], IdentityScenario.ValidPassword));
        var watcherBrowser = await identity.SignedInBrowserAsync(new(watcher.Value, emails[watcher.Value], IdentityScenario.ValidPassword));
        return new(factory, source, clock, service, seed with { Alice = ending, Bob = watcher.Value }, eventId, room, endingBrowser, watcherBrowser);
    }

    private static async Task PlayToCompletionAsync(ParticipationService service, Guid room, Guid first, Guid second, CancellationToken ct)
    {
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, first, true, ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, second, true, ct));
        var version = (await service.RoomAsync(room, first, ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, first, Guid.NewGuid(), version, "choose-option", "morning", "tea", ct));
        version = (await service.RoomAsync(room, second, ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, second, Guid.NewGuid(), version, "choose-option", "morning", "walk", ct));
    }

    private static async Task<Guid> RoundAsync(NpgsqlDataSource source, Guid eventId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select id from events.rounds where event_id=$1", eventId);
        return (Guid)(await query.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> SessionVersionAsync(RoomScene scene, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select version from experiences.game_sessions where id=$1", scene.Room);
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> ActiveReservationsAsync(RoomScene scene, CancellationToken ct)
    {
        await using var connection = await scene.Source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select count(*) from events.pairing_participants pp join experiences.game_sessions s on s.pairing_id=pp.pairing_id
            where s.id=$1 and pp.released_at is null
            """, scene.Room);
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }

    private sealed record RoomScene(DuoraWebFactory Factory, NpgsqlDataSource Source, FakeTimeProvider Clock, ParticipationService Service,
        ParticipationSeed Seed, Guid EventId, Guid Room, Browser Alice, Browser Bob) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            Bob.Dispose();
            await Factory.DisposeAsync();
            await Source.DisposeAsync();
        }
    }
}
