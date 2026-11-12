using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Caracterização de <c>BuildRoundAsync</c> pela porta pública <c>BuildRoundPairingsAsync</c>: grava o que a rodada
/// faz hoje em cada ramo, para que a extração de funções não mude o comportamento. Os testes só afirmam o estado
/// observável no banco e nos estágios do observador; nenhum espera por relógio real.
/// </summary>
public sealed class BuildRoundCharacterizationTests(PostgresFixture postgres)
{
    private const int Alice = 0;
    private const int Bob = 1;
    private const int Third = 2;
    private const int Fourth = 3;

    private static readonly TimeSpan InviteWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LateBuildLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OneMicrosecond = TimeSpan.FromTicks(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PausedMatchingLeavesTheRoundUntouchedAndObservesNoStage()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        var paused = new ParticipationService(source, scene.Clock,
            Options.Create(new ParticipationOptions { MatchingEnabled = false }));
        List<string> stages = [];
        paused.ObserveMatchingStage = (stage, _) => { stages.Add(stage); return Task.CompletedTask; };

        await paused.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Empty(stages);
        Assert.Equal(("Scheduled", 1L), await RoundAsync(source, scene.Round));
        Assert.Equal(("Published", scene.EventVersion), await EventAsync(source, scene.Event));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
    }

    [Fact]
    public async Task AnUnknownRoundBuildsNothingAndDoesNotFail()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);

        await scene.Service.BuildRoundPairingsAsync(Guid.NewGuid(), Ct);

        Assert.Equal(("Scheduled", 1L), await RoundAsync(source, scene.Round));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
    }

    [Theory]
    [InlineData("Published", true)]
    [InlineData("Running", true)]
    [InlineData("Completed", false)]
    [InlineData("Cancelled", false)]
    public async Task OnlyAPublishedOrRunningEventBuildsItsRound(string eventState, bool builds)
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        await ExecuteAsync(source, "update events.events set state=$2 where id=$1", scene.Event, eventState);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(builds ? 1L : 0L, await PairingCountAsync(source, scene.Round));
        Assert.Equal(builds ? "Inviting" : "Scheduled", (await RoundAsync(source, scene.Round)).State);
    }

    [Fact]
    public async Task ARoundThatHasNotStartedYetIsLeftScheduled()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob], fromStart: -OneMicrosecond);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(("Scheduled", 1L), await RoundAsync(source, scene.Round));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
    }

    [Theory]
    [InlineData("Inviting")]
    [InlineData("Closed")]
    [InlineData("Cancelled")]
    public async Task ARoundThatIsNoLongerScheduledIsNotBuiltAgain(string roundState)
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        await ExecuteAsync(source, "update events.rounds set state=$2 where id=$1", scene.Round, roundState);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal((roundState, 1L), await RoundAsync(source, scene.Round));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
        Assert.Equal(("Published", scene.EventVersion), await EventAsync(source, scene.Event));
    }

    [Fact]
    public async Task ARoundBuiltExactlyAtTheLateLimitStillPairs()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob], fromStart: LateBuildLimit);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(("Inviting", 2L), await RoundAsync(source, scene.Round));
        Assert.Equal(1L, await PairingCountAsync(source, scene.Round));
    }

    [Fact]
    public async Task ARoundBuiltAMicrosecondAfterTheLateLimitIsCancelledWithoutPairingsOrRunningEvent()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob], fromStart: LateBuildLimit + OneMicrosecond);
        List<string> stages = [];
        scene.Service.ObserveMatchingStage = (stage, _) => { stages.Add(stage); return Task.CompletedTask; };

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(("Cancelled", 2L), await RoundAsync(source, scene.Round));
        Assert.Equal(("Published", scene.EventVersion), await EventAsync(source, scene.Event));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
        Assert.Equal(["CandidatesRead", "CandidatesGuarded"], stages);
    }

    [Fact]
    public async Task ABuiltRoundReportsItsThreeStagesInOrder()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        List<string> stages = [];
        scene.Service.ObserveMatchingStage = (stage, _) => { stages.Add(stage); return Task.CompletedTask; };

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(["CandidatesRead", "CandidatesGuarded", "PairingsWritten"], stages);
    }

    [Fact]
    public async Task APairedRoundWritesRoomInviteJobParticipantsAndMovesRoundAndEventForward()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        await ExecuteAsync(source, "update identity.users set preferred_locale='en-US' where id=$1", scene.Seed.Alice);
        await ExecuteAsync(source, "update identity.users set preferred_locale='pt-BR' where id=$1", scene.Seed.Bob);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(("Inviting", 2L), await RoundAsync(source, scene.Round));
        Assert.Equal(("Running", scene.EventVersion + 1), await EventAsync(source, scene.Event));
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var pairing = Command(connection, """
            select p.id,p.event_id,p.algorithm_version,p.communication_language,p.communication_policy_version,p.created_at,
              gs.id,gs.state,gs.experience_version_id,gs.invite_expires_at
            from events.pairings p join experiences.game_sessions gs on gs.pairing_id=p.id where p.round_id=$1
            """, scene.Round);
        await using var reader = await pairing.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var pairingId = reader.GetGuid(0);
        var room = reader.GetGuid(6);
        Assert.Equal(scene.Event, reader.GetGuid(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal("en", reader.GetString(3));
        Assert.Equal(1, reader.GetInt32(4));
        Assert.Equal(scene.Starts, reader.GetFieldValue<DateTimeOffset>(5));
        Assert.Equal("Invited", reader.GetString(7));
        Assert.Equal(scene.Seed.ContentId, reader.GetGuid(8));
        Assert.Equal(scene.Starts + InviteWindow, reader.GetFieldValue<DateTimeOffset>(9));
        Assert.False(await reader.ReadAsync(Ct));
        await reader.CloseAsync();

        Assert.Equal(
            new HashSet<Guid> { scene.Seed.Alice, scene.Seed.Bob },
            await UsersAsync(connection, "select user_id from events.pairing_participants where pairing_id=$1 and round_id=$2 and released_at is null", pairingId, scene.Round));
        Assert.Equal("en-US", await LocaleAsync(connection, room, scene.Seed.Alice));
        Assert.Equal("pt-BR", await LocaleAsync(connection, room, scene.Seed.Bob));
        await using var jobs = Command(connection, "select due_at,available_at,status from ops.jobs where kind='expire-invite' and aggregate_id=$1", room);
        await using var job = await jobs.ExecuteReaderAsync(Ct);
        Assert.True(await job.ReadAsync(Ct));
        Assert.Equal(scene.Starts + InviteWindow, job.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(scene.Starts + InviteWindow, job.GetFieldValue<DateTimeOffset>(1));
        Assert.Equal("Pending", job.GetString(2));
        Assert.False(await job.ReadAsync(Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ARoundWithFewerThanTwoCandidatesStillStartsWithoutPairings(int checkedIn)
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, new[] { Alice }.Take(checkedIn));

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
        Assert.Equal(("Inviting", 2L), await RoundAsync(source, scene.Round));
        Assert.Equal(("Running", scene.EventVersion + 1), await EventAsync(source, scene.Event));
    }

    [Fact]
    public async Task OnlyCheckedInRegistrationsAreCandidates()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Third], confirmedOnly: [Bob]);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        var pair = Assert.Single(await PairsAsync(source, scene.Round));
        Assert.Equal(new HashSet<Guid> { scene.Seed.Alice, scene.Seed.Third }, pair);
    }

    [Theory]
    [InlineData("update identity.users set email_confirmed=false where id=$1")]
    [InlineData("update identity.users set status='Restricted' where id=$1")]
    [InlineData("update identity.selfie_verifications set state='Expired' where user_id=$1")]
    public async Task ACandidateWhoIsNoLongerEligibleIsLeftOutOfThePairing(string breakEligibility)
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        await ExecuteAsync(source, breakEligibility, scene.Seed.Bob);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
        Assert.Equal(("Inviting", 2L), await RoundAsync(source, scene.Round));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABlockInEitherDirectionKeepsTwoCandidatesApart(bool aliceBlocksBob)
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        var (blocker, blocked) = aliceBlocksBob ? (scene.Seed.Alice, scene.Seed.Bob) : (scene.Seed.Bob, scene.Seed.Alice);
        await ExecuteAsync(source, "insert into safety.blocks(id,blocker_id,blocked_id,created_at) values(gen_random_uuid(),$1,$2,now())", blocker, blocked);

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
    }

    [Fact]
    public async Task AV2RoundIgnoresRegionalPreferencesThatAV3RoundWouldEnforce()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob]);
        await DeclareRegionAsync(source, scene.Seed.Alice, "BR", "countries", "US");
        await DeclareRegionAsync(source, scene.Seed.Bob, "BR", "unrestricted");

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        var pair = Assert.Single(await PairsAsync(source, scene.Round));
        Assert.Equal(new HashSet<Guid> { scene.Seed.Alice, scene.Seed.Bob }, pair);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var version = Command(connection, "select algorithm_version from events.pairings where round_id=$1", scene.Round);
        Assert.Equal(2, await version.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task AStoredRegionalModeThatTheCodeDoesNotKnowFailsTheWholeBuild()
    {
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var owner = NpgsqlDataSource.Create(database.MigratorConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob], database.AppConnectionString);
        await ExecuteAsync(source, "update events.rounds set algorithm_version=3 where id=$1", scene.Round);
        await DeclareRegionAsync(source, scene.Seed.Alice, "BR", "unrestricted");
        await DeclareRegionAsync(source, scene.Seed.Bob, "BR", "unrestricted");
        // O CHECK da coluna torna o valor inalcançável pela API; sem ele o ramo defensivo do código vira observável.
        await ExecuteAsync(owner, "alter table profiles.regional_preferences drop constraint ck_regional_preferences_mode");
        await ExecuteAsync(source, "update profiles.regional_preferences set mode='everywhere' where user_id=$1", scene.Seed.Bob);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scene.Service.BuildRoundPairingsAsync(scene.Round, Ct));

        Assert.Equal("Invalid stored regional mode.", failure.Message);
        Assert.Equal(("Scheduled", 1L), await RoundAsync(source, scene.Round));
        Assert.Equal(0L, await PairingCountAsync(source, scene.Round));
    }

    [Fact]
    public async Task CandidatesLeftUnpairedInAnEarlierRoundOutrankThoseWhoWereAlreadyPaired()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        var scene = await CreateSceneAsync(source, [Alice, Bob, Third, Fourth]);
        var fourth = scene.Users[Fourth];
        var earlier = Guid.CreateVersion7();
        await ExecuteAsync(source, "update events.rounds set number=2 where id=$1", scene.Round);
        await ExecuteAsync(source, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,1,'Closed',$3,$4,7)",
            earlier, scene.Event, scene.Starts.AddMinutes(-12), scene.Starts);
        // Alice e Bob esperam mais, mas já tiveram par na rodada anterior; Third e o quarto ficaram sem par nela.
        var order = new[] { scene.Seed.Alice, scene.Seed.Bob, scene.Seed.Third, fourth };
        for (var i = 0; i < order.Length; i++)
        {
            await ExecuteAsync(source, "update events.registrations set checked_in_at=$3 where event_id=$1 and user_id=$2",
                scene.Event, order[i], scene.Starts.AddMinutes(-40 + i));
        }
        var priorPairing = Guid.CreateVersion7();
        await ExecuteAsync(source, """
            insert into events.pairings(id,round_id,event_id,user_low,user_high,algorithm_version,communication_language,communication_policy_version,created_at,released_at)
            values($1,$2,$3,least($4::uuid,$5::uuid),greatest($4::uuid,$5::uuid),2,'en',1,$6,$6)
            """, priorPairing, earlier, scene.Event, scene.Seed.Alice, scene.Seed.Bob, scene.Starts.AddMinutes(-12));
        foreach (var user in new[] { scene.Seed.Alice, scene.Seed.Bob })
        {
            await ExecuteAsync(source, "insert into events.pairing_participants(pairing_id,user_id,round_id,released_at) values($1,$2,$3,$4)",
                priorPairing, user, earlier, scene.Starts);
        }

        await scene.Service.BuildRoundPairingsAsync(scene.Round, Ct);

        var pair = Assert.Single(await PairsAsync(source, scene.Round));
        Assert.Equal(new HashSet<Guid> { scene.Seed.Third, fourth }, pair);
    }

    private sealed record Scene(ParticipationService Service, FakeTimeProvider Clock, ParticipationSeed Seed, DateTimeOffset Starts,
        IReadOnlyList<Guid> Users, long EventVersion)
    {
        public Guid Round => Seed.Rounds[0];

        public Guid Event => Seed.Events[0];
    }

    /// <summary>
    /// Sementeia o evento, inscreve e faz check-in de quem foi pedido (e só inscreve quem está em <paramref name="confirmedOnly"/>)
    /// e deixa o relógio no início da rodada mais <paramref name="fromStart"/>. O índice <see cref="Fourth"/> cria um participante a mais.
    /// </summary>
    private async Task<Scene> CreateSceneAsync(NpgsqlDataSource source, IEnumerable<int> checkedIn,
        string? connectionString = null, IEnumerable<int>? confirmedOnly = null,
        TimeSpan fromStart = default)
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString ?? postgres.ConnectionString, now, Ct,
            Guid.NewGuid().ToString("N"), declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        Guid[] users = [seed.Alice, seed.Bob, seed.Third, .. checkedIn.Contains(Fourth) ? [await AddParticipantAsync(source, now)] : Array.Empty<Guid>()];
        foreach (var index in confirmedOnly ?? [])
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], users[index], false, Ct));
        }
        foreach (var index in checkedIn)
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], users[index], false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], users[index], true, Ct));
        }
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select starts_at from events.rounds where id=$1", seed.Rounds[0]);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var starts = reader.GetFieldValue<DateTimeOffset>(0);
        await reader.CloseAsync();
        await using var eventQuery = Command(connection, "select version from events.events where id=$1", seed.Events[0]);
        var eventVersion = (long)(await eventQuery.ExecuteScalarAsync(Ct))!;
        clock.SetUtcNow(starts + fromStart);
        return new(service, clock, seed, starts, users, eventVersion);
    }

    private static async Task<Guid> AddParticipantAsync(NpgsqlDataSource source, DateTimeOffset now)
    {
        var user = Guid.NewGuid();
        var email = $"fourth-{user:N}@example.com";
        await ExecuteAsync(source, """
            insert into identity.users(id,status,created_at,user_name,normalized_user_name,email,normalized_email,email_confirmed,
              security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
            values($1,'Active',$2,$3,upper($3),$3,upper($3),true,$4,$5,false,false,true,0)
            """, user, now, email, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await ExecuteAsync(source, "insert into identity.user_roles(user_id,role_id) select $1,id from identity.roles where normalized_name='PARTICIPANT'", user);
        await ExecuteAsync(source, """
            insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,expires_at,completed_at,version)
            values($1,$2,'Passed','test',$3,$3,$3,1)
            """, Guid.NewGuid(), user, now);
        await ExecuteAsync(source, "insert into profiles.preferences(user_id,allow_different_display_locales,version,updated_at) values($1,true,1,$2)", user, now);
        await ExecuteAsync(source, "insert into profiles.communication_languages(user_id,language_code) values($1,'en')", user);
        return user;
    }

    private static async Task DeclareRegionAsync(NpgsqlDataSource source, Guid user, string country, string mode, string? accepted = null)
    {
        await ExecuteAsync(source, "insert into profiles.profiles(user_id,country_code,version,updated_at) values($1,$2,1,now()) on conflict(user_id) do update set country_code=$2", user, country);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await Execute(connection, "insert into profiles.regional_preferences(user_id,mode,version,updated_at) values($1,$2,1,now())", Ct, user, mode);
        if (accepted is not null)
        {
            await Execute(connection, "insert into profiles.regional_preference_countries(user_id,country_code) values($1,$2)", Ct, user, accepted);
        }
        await transaction.CommitAsync(Ct);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await Execute(connection, sql, Ct, values);
    }

    private static async Task<(string State, long Version)> RoundAsync(NpgsqlDataSource source, Guid round) =>
        await StateAsync(source, "select state,version from events.rounds where id=$1", round);

    private static async Task<(string State, long Version)> EventAsync(NpgsqlDataSource source, Guid eventId) =>
        await StateAsync(source, "select state,version from events.events where id=$1", eventId);

    private static async Task<(string State, long Version)> StateAsync(NpgsqlDataSource source, string sql, Guid id)
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, sql, id);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<long> PairingCountAsync(NpgsqlDataSource source, Guid round)
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select count(*) from events.pairings where round_id=$1", round);
        return (long)(await query.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<HashSet<Guid>>> PairsAsync(NpgsqlDataSource source, Guid round)
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select user_low,user_high from events.pairings where round_id=$1", round);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        List<HashSet<Guid>> pairs = [];
        while (await reader.ReadAsync(Ct))
        {
            pairs.Add([reader.GetGuid(0), reader.GetGuid(1)]);
        }

        return pairs;
    }

    private static async Task<HashSet<Guid>> UsersAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var query = Command(connection, sql, values);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        HashSet<Guid> users = [];
        while (await reader.ReadAsync(Ct))
        {
            users.Add(reader.GetGuid(0));
        }

        return users;
    }

    private static async Task<string> LocaleAsync(NpgsqlConnection connection, Guid room, Guid user)
    {
        await using var query = Command(connection, "select display_locale from experiences.game_participants where game_session_id=$1 and user_id=$2", room, user);
        return (string)(await query.ExecuteScalarAsync(Ct))!;
    }
}
