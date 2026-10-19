using System.Net;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class ParticipationAuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task BlockAbortsAndReleasesInSameTransactionAndDeniesHttpReadAndReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        await using var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value;
        await service.RespondAsync(room, seed.Alice, true, ct);
        await service.RespondAsync(room, seed.Bob, true, ct);
        var version = (await service.RoomAsync(room, seed.Alice, ct))!.Version;
        var action = Guid.NewGuid();
        Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, seed.Alice, action, version, "choose-option", "morning", "tea", ct));
        var identity = new IdentityScenario(connectionString, factory);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        using var third = await identity.SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));
        using var thirdRead = await third.GetAsync($"/api/v1/rooms/{room}");
        Assert.Equal(HttpStatusCode.NotFound, thirdRead.StatusCode);
        using var blocked = await alice.PutAsync($"/api/v1/me/blocks/{seed.Bob}");
        Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
        using var read = await alice.GetAsync($"/api/v1/rooms/{room}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var replay = await alice.PostJsonAsync($"/api/v1/rooms/{room}/actions", new { actionId = action, expectedVersion = version.ToString(System.Globalization.CultureInfo.InvariantCulture), type = "choose-option", cardId = "morning", optionId = "tea" });
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var query = Command(c, "select count(*) from events.pairing_participants where pairing_id=(select pairing_id from experiences.game_sessions where id=$1) and released_at is null", room);
        Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
        await using var state = Command(c, "select state from experiences.game_sessions where id=$1", room);
        Assert.Equal("Aborted", await state.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData("undeclared")]
    [InlineData("different-language")]
    [InlineData("different-locale")]
    public async Task UndeclaredOrIncompatibleParticipantsNeverReceiveInvitation(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            if (scenario == "undeclared")
            {
                await Execute(c, "delete from profiles.communication_languages where user_id=$1", ct, seed.Bob);
                await Execute(c, "delete from profiles.preferences where user_id=$1", ct, seed.Bob);
            }
            else if (scenario == "different-language")
            {
                await Execute(c, "update profiles.communication_languages set language_code='pt' where user_id=$1", ct, seed.Bob);
            }
            else
            {
                await Execute(c, "update identity.users set preferred_locale='pt-BR' where id=$1", ct, seed.Bob);
                await Execute(c, "update profiles.preferences set allow_different_display_locales=false where user_id=$1", ct, seed.Alice);
            }
        }
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        Assert.Equal("Waiting", (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.State);
        Assert.Null((await service.StatusAsync(seed.Events[0], seed.Bob, ct))!.RoomId);
    }
}
