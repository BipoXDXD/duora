using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>A saída da sala (API-R04) pela rota HTTP real: é o controle de segurança pessoal, então vale sempre que a pessoa estiver na sala.</summary>
public sealed class RoomExitTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Invited")]
    [InlineData("Active")]
    public async Task ExitAbortsTheRoomReleasesBothAndIsRepeatable(string openState)
    {
        await using var scene = await OpenRoomAsync(openState);
        var before = await scene.RoomRowAsync();
        Assert.Equal(openState, before.State);
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        using var bob = await scene.SignInAsync("bob", scene.Seed.Bob);
        var exitedAt = Truncated(scene.Clock.GetUtcNow());

        using (var exit = await alice.PostJsonAsync(scene.ExitPath, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        }

        var after = (State: "Aborted", Version: before.Version + 1);
        Assert.Equal(after, await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        Assert.Equal(exitedAt, await scene.DepartedAtAsync(scene.Seed.Alice));
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));

        scene.Clock.Advance(TimeSpan.FromSeconds(5));
        using (var repeated = await alice.PostJsonAsync(scene.ExitPath, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        }

        Assert.Equal(after, await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        Assert.Equal(exitedAt, await scene.DepartedAtAsync(scene.Seed.Alice));
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));

        using var aliceRead = await alice.GetAsync(scene.RoomPath);
        Assert.Equal(HttpStatusCode.NotFound, aliceRead.StatusCode);
        Assert.DoesNotContain(scene.Room.ToString(), await aliceRead.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        using var bobRead = await bob.GetAsync(scene.RoomPath);
        Assert.Equal(HttpStatusCode.OK, bobRead.StatusCode);
        using var body = JsonDocument.Parse(await bobRead.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Aborted", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ExitFromACompletedRoomKeepsItCompletedAndOnlyRecordsTheDeparture()
    {
        await using var scene = await OpenRoomAsync("Completed");
        var completed = await scene.RoomRowAsync();
        Assert.Equal("Completed", completed.State);
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);

        using var exit = await alice.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        Assert.Equal(completed, await scene.RoomRowAsync());
        Assert.NotNull(await scene.DepartedAtAsync(scene.Seed.Alice));
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));
    }

    [Fact]
    public async Task ExitStillWorksWhenThePartnerIsIneligible()
    {
        await using var scene = await OpenRoomAsync("Active");
        var before = await scene.RoomRowAsync();
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        await scene.ExecuteAsync("update identity.users set status='Restricted' where id=$1", scene.Seed.Bob);

        using var exit = await alice.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        Assert.Equal(("Aborted", before.Version + 1), await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        Assert.NotNull(await scene.DepartedAtAsync(scene.Seed.Alice));
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitAfterABlockAnswersNoContentAndAddsNoSecondEffect(bool blockerLeaves)
    {
        await using var scene = await OpenRoomAsync("Active");
        using var alice = await scene.SignInAsync("alice", scene.Seed.Alice);
        using var bob = await scene.SignInAsync("bob", scene.Seed.Bob);
        using (var blocked = await alice.PutAsync($"/api/v1/me/blocks/{scene.Seed.Bob}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
        }

        var aborted = await scene.RoomRowAsync();
        Assert.Equal("Aborted", aborted.State);
        var leaving = blockerLeaves ? alice : bob;
        var leaver = blockerLeaves ? scene.Seed.Alice : scene.Seed.Bob;
        var other = blockerLeaves ? scene.Seed.Bob : scene.Seed.Alice;

        using var exit = await leaving.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        Assert.Equal(aborted, await scene.RoomRowAsync());
        Assert.Equal(0L, await scene.ActiveReservationsAsync());
        Assert.NotNull(await scene.DepartedAtAsync(leaver));
        Assert.Null(await scene.DepartedAtAsync(other));
    }

    [Theory]
    [InlineData("Invited")]
    [InlineData("Active")]
    public async Task ExitByAnAccountOutsideTheRoomAnswersNotFoundWithNoDataAndLeavesTheRoomIntact(string openState)
    {
        await using var scene = await OpenRoomAsync(openState);
        var before = await scene.RoomRowAsync();
        using var outsider = await scene.SignInAsync("third", scene.Seed.Third);

        using var exit = await outsider.PostJsonAsync(scene.ExitPath, null);

        Assert.Equal(HttpStatusCode.NotFound, exit.StatusCode);
        var body = await exit.Content.ReadAsStringAsync(Ct);
        foreach (var secret in new[] { scene.Room.ToString(), scene.Seed.Alice.ToString(), scene.Seed.Bob.ToString(), "alice@", "bob@" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await scene.RoomRowAsync());
        Assert.Equal(2L, await scene.ActiveReservationsAsync());
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Alice));
        Assert.Null(await scene.DepartedAtAsync(scene.Seed.Bob));
    }

    private static DateTimeOffset Truncated(DateTimeOffset instant) => new(instant.Ticks - (instant.Ticks % 10), TimeSpan.Zero);

    private async Task<OpenRoom> OpenRoomAsync(string openState)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, Ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, Ct))!.RoomId!.Value;
        if (openState != "Invited")
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, Ct));
        }

        if (openState == "Completed")
        {
            var firstVersion = (await service.RoomAsync(room, seed.Alice, Ct))!.Version;
            Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Alice, Guid.NewGuid(), firstVersion, "choose-option", "morning", "tea", Ct));
            var secondVersion = (await service.RoomAsync(room, seed.Bob, Ct))!.Version;
            Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Bob, Guid.NewGuid(), secondVersion, "choose-option", "morning", "walk", Ct));
        }

        return new OpenRoom(source, clock, factory, new IdentityScenario(connectionString, factory), seed, room);
    }

    private sealed class OpenRoom(NpgsqlDataSource source, FakeTimeProvider clock, DuoraWebFactory factory, IdentityScenario identity, ParticipationSeed seed, Guid room) : IAsyncDisposable
    {
        public FakeTimeProvider Clock => clock;

        public ParticipationSeed Seed => seed;

        public Guid Room => room;

        public string RoomPath => $"/api/v1/rooms/{room}";

        public string ExitPath => $"{RoomPath}/exit";

        public Task<Browser> SignInAsync(string name, Guid user) =>
            identity.SignedInBrowserAsync(new(user, $"{name}@example.com", IdentityScenario.ValidPassword));

        public async Task<(string State, long Version)> RoomRowAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select state,version from experiences.game_sessions where id=$1", room);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt64(1));
        }

        public async Task<long> ActiveReservationsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", new[] { seed.Alice, seed.Bob });
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task<DateTimeOffset?> DepartedAtAsync(Guid user)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select departed_at from experiences.game_participants where game_session_id=$1 and user_id=$2", room, user);
            var value = await query.ExecuteScalarAsync(Ct);
            return value is DateTime departedAt ? new DateTimeOffset(departedAt) : null;
        }

        public async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            Assert.Equal(1, await Execute(connection, sql, Ct, values));
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
