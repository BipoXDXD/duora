using Duora.Core.Profiles;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class RegionalMatchingTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("countries", "countries", "BR", "BR", true)]
    [InlineData("countries", "unrestricted", "BR", null, false)]
    [InlineData("unrestricted", "countries", null, "BR", false)]
    [InlineData("countries", "unrestricted", null, "BR", true)]
    [InlineData("unrestricted", "unrestricted", null, null, true)]
    [InlineData(null, "unrestricted", "BR", "BR", false)]
    [InlineData("unrestricted", null, "BR", "BR", false)]
    public async Task PersistedV3UsesExplicitChoicesAndDeclaredCountries(string? firstMode, string? secondMode,
        string? firstCountry, string? secondCountry, bool paired)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var (seed, clock, service) = await Setup(source, firstMode, secondMode, firstCountry, secondCountry, ct);
        // No cutover configured: execution must still follow the persisted round version, including after restart.
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        await new ParticipationService(source, clock).BuildRoundPairingsAsync(seed.Rounds[0], ct);
        var room = (await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId;
        Assert.Equal(paired, room.HasValue);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, "select algorithm_version,communication_language,communication_policy_version from events.pairings where round_id=$1", seed.Rounds[0]);
        await using var reader = await query.ExecuteReaderAsync(ct);
        Assert.Equal(paired, await reader.ReadAsync(ct));
        if (paired)
        {
            Assert.Equal(3, reader.GetInt32(0));
            Assert.Equal("en", reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(2));
            var view = await service.RoomAsync(room!.Value, seed.Alice, ct);
            Assert.NotNull(view);
            Assert.Equal("en", view.Language);
        }
    }

    [Theory]
    [InlineData(false, "modern")]
    [InlineData(true, "modern")]
    [InlineData(false, "legacy")]
    [InlineData(true, "legacy")]
    [InlineData(false, "preference")]
    [InlineData(true, "preference")]
    public async Task EditsCommittedBeforeGuardsAreObservedAndLaterEditsPreserveConfirmedRooms(bool afterGuards, string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var (seed, clock, service) = await Setup(source, "countries", "unrestricted", kind == "preference" ? null : "BR", "BR", ct);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ObserveMatchingStage = async (stage, token) =>
        {
            if (stage == (afterGuards ? "CandidatesGuarded" : "CandidatesRead"))
            { reached.SetResult(); await resume.Task.WaitAsync(token); }
        };
        var build = service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        await reached.Task.WaitAsync(ct);
        Task update;
        if (kind == "preference")
        {
            Assert.True(RegionalPreference.TryCreate("countries", ["BR"], out var value));
            update = new RegionalPreferenceStore(source, clock).UpdateOwnAsync(seed.Bob, 1, value!, ct);
        }
        else
        {
            var edit = kind == "legacy" ? new ProfileEdit(null, null, new(null)) : new(null, null, null, new(null, null));
            update = new ProfileStore(source, clock).UpdateOwnAsync(seed.Bob, 1, edit, ct);
        }
        if (afterGuards)
        {
            try
            { await WaitForGuardWaiter(source, update, ct); }
            finally { resume.TrySetResult(); }
            await Task.WhenAll(build, update);
            var room = Assert.NotNull((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
            Assert.NotNull(await service.RoomAsync(room, seed.Alice, ct));
            Assert.NotNull(await service.RoomAsync(room, seed.Bob, ct));
        }
        else
        {
            try
            { await update; }
            finally { resume.TrySetResult(); }
            await build;
            Assert.Null((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
        }
    }

    [Theory]
    [InlineData("language")]
    [InlineData("locale")]
    [InlineData("block")]
    [InlineData("restricted")]
    [InlineData("pending-language")]
    public async Task V3KeepsAllOtherEligibilityFilters(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var (seed, _, service) = await Setup(source, "unrestricted", "unrestricted", "BR", "BR", ct);
        await using var c = await source.OpenConnectionAsync(ct);
        var sql = reason switch
        {
            "language" => "update profiles.communication_languages set language_code='pt' where user_id=$1",
            "locale" => "update identity.users set preferred_locale='pt-BR' where id=$1; update profiles.preferences set allow_different_display_locales=false where user_id=$1",
            "block" => "insert into safety.blocks(id,blocker_id,blocked_id,created_at) values(gen_random_uuid(),$1,$2,now())",
            "restricted" => "update identity.users set status='Restricted' where id=$1",
            _ => "delete from profiles.communication_languages where user_id=$1; delete from profiles.preferences where user_id=$1"
        };
        // Positional parameters cannot be shared across SQL statements; use one command per statement.
        foreach (var statement in sql.Split(';'))
        { await Execute(c, statement, ct, statement.Contains("$2", StringComparison.Ordinal) ? [seed.Bob, seed.Alice] : [seed.Bob]); }
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        Assert.Null((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
    }

    [Fact]
    public async Task ConcurrentCoordinatorsAndOverlappingV3EventsReserveOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var (seed, clock, service) = await Setup(source, "unrestricted", "unrestricted", null, null, ct);
        await using var c = await source.OpenConnectionAsync(ct);
        await Execute(c, "update events.events set starts_at=$2 where id=$1", ct, seed.Events[1], clock.GetUtcNow());
        await Execute(c, "update events.rounds set starts_at=$2,deadline_at=$3,algorithm_version=3 where id=$1", ct, seed.Rounds[1], clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(12));
        // Register before the start using a separate clock; the shared reservation remains authoritative.
        var registering = new ParticipationService(source, new FakeTimeProvider(clock.GetUtcNow().AddMinutes(-5)));
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await registering.RegisterAsync(seed.Events[1], user, false, ct);
            await registering.RegisterAsync(seed.Events[1], user, true, ct);
        }
        await Task.WhenAll(service.BuildRoundPairingsAsync(seed.Rounds[0], ct),
            new ParticipationService(source, clock).BuildRoundPairingsAsync(seed.Rounds[0], ct),
            service.BuildRoundPairingsAsync(seed.Rounds[1], ct));
        await using var query = Command(c, "select count(*) from events.pairing_participants where user_id=$1 and released_at is null", seed.Alice);
        Assert.Equal(1L, await query.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task CutoverOnlyLabelsNewRoundsAndNeverReinterpretsAnExistingRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        var admin = await RoleSeparationTests.InsertUserAsync(connection);
        await RoleSeparationTests.GrantAsync(connection, admin, "product_admin");
        await using var contentQuery = Command(connection, "select experience_version_id from events.events where id=$1", seed.Events[0]);
        var content = (Guid)(await contentQuery.ExecuteScalarAsync(ct))!;
        var start = now.AddHours(1);
        var service = new ParticipationService(source, new FakeTimeProvider(now), Options.Create(new ParticipationOptions { RegionalMatchingStartsAt = start.AddMinutes(12) }));
        var published = Assert.NotNull(await service.PublishAsync("Synthetic cutover", start, 2, content, admin, ct));
        await using (var query = Command(connection, "select algorithm_version from events.rounds where event_id=$1 order by number", published))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            List<int> versions = [];
            while (await reader.ReadAsync(ct))
            { versions.Add(reader.GetInt32(0)); }
            Assert.Equal([2, 3, 3], versions);
        }
        // Already scheduled V2 must retain V2, even with a configured cutover before its start.
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        var replay = new ParticipationService(source, new FakeTimeProvider(now.AddMinutes(5)),
            Options.Create(new ParticipationOptions { RegionalMatchingStartsAt = now }));
        await replay.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        Assert.NotNull((await replay.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
        await using var pair = Command(connection, "select algorithm_version from events.pairings where round_id=$1", seed.Rounds[0]);
        Assert.Equal(2, await pair.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task SubdivisionsDoNotRestrictPairsAndPreviouslyPairedUsersCannotRepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var (seed, clock, service) = await Setup(source, "countries", "countries", "BR", "BR", ct);
        var profiles = new ProfileStore(source, clock);
        Assert.NotNull(await profiles.UpdateOwnAsync(seed.Alice, 1, new(null, null, null, new("BR", "BR-SP")), ct));
        Assert.NotNull(await profiles.UpdateOwnAsync(seed.Bob, 1, new(null, null, null, new("BR", "BR-RJ")), ct));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        Assert.NotNull((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId);
        await using var c = await source.OpenConnectionAsync(ct);
        await Execute(c, "update events.pairing_participants set released_at=$2 where user_id=any($1)", ct, new[] { seed.Alice, seed.Bob }, clock.GetUtcNow());
        var nextRound = Guid.CreateVersion7();
        var starts = clock.GetUtcNow().AddMinutes(12);
        await Execute(c, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed,algorithm_version) values($1,$2,2,'Scheduled',$3,$4,43,3)",
            ct, nextRound, seed.Events[0], starts, starts.AddMinutes(12));
        clock.Advance(TimeSpan.FromMinutes(12));
        await service.BuildRoundPairingsAsync(nextRound, ct);
        await using var pairs = Command(c, "select count(*) from events.pairings where round_id=$1", nextRound);
        Assert.Equal(0L, await pairs.ExecuteScalarAsync(ct));
    }

    private async Task<(ParticipationSeed Seed, FakeTimeProvider Clock, ParticipationService Service)> Setup(
        NpgsqlDataSource source, string? firstMode, string? secondMode, string? firstCountry, string? secondCountry, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var c = await source.OpenConnectionAsync(ct);
        await Execute(c, "update events.rounds set algorithm_version=3 where id=$1", ct, seed.Rounds[0]);
        foreach (var (user, mode, country) in new[] { (seed.Alice, firstMode, firstCountry), (seed.Bob, secondMode, secondCountry) })
        {
            await Execute(c, "insert into profiles.profiles(user_id,country_code,version,updated_at) values($1,$2,1,$3)", ct, user, (object?)country ?? DBNull.Value, now);
            if (mode is not null)
            {
                await using var tx = await c.BeginTransactionAsync(ct);
                await Execute(c, "insert into profiles.regional_preferences(user_id,mode,version,updated_at) values($1,$2,1,$3)", ct, user, mode, now);
                if (mode == "countries")
                { await Execute(c, "insert into profiles.regional_preference_countries(user_id,country_code) values($1,'BR')", ct, user); }
                await tx.CommitAsync(ct);
            }
        }
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        return (seed, clock, service);
    }

    private static async Task WaitForGuardWaiter(NpgsqlDataSource source, Task writer, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var c = await source.OpenConnectionAsync(linked.Token);
        while (true)
        {
            await using var query = Command(c, "select exists(select 1 from pg_stat_activity where datname=current_database() and wait_event_type='Lock' and query like '%user_guards%')");
            if ((bool)(await query.ExecuteScalarAsync(linked.Token))!)
            { return; }
            Assert.False(writer.IsCompleted, "Writer bypassed matching guards.");
            await Task.Delay(10, linked.Token);
        }
    }
}
