using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Teto de 8 tentativas de <c>ops.jobs</c> e o que acontece com as reservas depois dele. Reservas nascem na transação de
/// <c>build-round</c>; <c>close-round</c> é o último job que as libera.
/// </summary>
public sealed class ParticipationJobExhaustionTests(PostgresFixture postgres)
{
    private static readonly TimeSpan PastTheRetryDelay = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan PastTheLease = TimeSpan.FromSeconds(61);
    private static readonly TimeSpan PastTheRoundDeadline = TimeSpan.FromMinutes(13);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("retry", "Failed|8|ProcessingFailed")]
    [InlineData("lease", "Failed|9|LeaseExpiredExhausted")]
    public async Task AParticipationJobFailsAfterTheEighthAttemptAndIsNoLongerClaimed(string path, string expected)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        var jobId = await scenario.OnlyJobDueIsAsync("expire-invite", scenario.Room);
        scenario.Clock.Advance(TimeSpan.FromSeconds(61));

        await scenario.ExhaustAsync(path, "expire-invite");

        Assert.Equal(expected, await scenario.JobAsync(jobId));
        scenario.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await scenario.Service.ClaimJobAsync(Ct));
        Assert.Equal(expected, await scenario.JobAsync(jobId));
    }

    /// <summary>O job que falha não libera nada: quem cobre é o <c>close-round</c> da rodada, sem compensação própria.</summary>
    [Theory]
    [InlineData("expire-invite", "retry", false)]
    [InlineData("expire-invite", "lease", false)]
    [InlineData("game-deadline", "retry", true)]
    [InlineData("game-deadline", "lease", true)]
    public async Task AFailedRoomJobDoesNotKeepReservationsForever(string kind, string path, bool active)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        if (active)
        {
            await scenario.AcceptBothAsync();
        }

        await scenario.OnlyJobDueIsAsync(kind, scenario.Room);
        scenario.Clock.Advance(TimeSpan.FromMinutes(9));
        await scenario.ExhaustAsync(path, kind);
        Assert.Equal(2, await scenario.ActiveReservationsAsync());

        await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        var closing = await scenario.Service.ClaimJobAsync(Ct);
        Assert.NotNull(closing);
        Assert.True(await scenario.Service.CompleteJobAsync(closing, Ct));

        Assert.Equal(0, await scenario.ActiveReservationsAsync());
        Assert.Equal("Expired", await scenario.RoomStateAsync());
    }

    [Theory]
    [InlineData("retry", false)]
    [InlineData("retry", true)]
    [InlineData("lease", false)]
    [InlineData("lease", true)]
    public async Task AnExhaustedCloseRoundReleasesOpenRoomsInTheSameCommit(string path, bool active)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        if (active)
        {
            await scenario.AcceptBothAsync();
        }

        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);

        await scenario.ExhaustAsync(path, "close-round");

        Assert.StartsWith("Failed|", await scenario.JobAsync(jobId), StringComparison.Ordinal);
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
        Assert.Equal("Expired", await scenario.RoomStateAsync());
        var alert = Assert.Single(scenario.Logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("close-round", alert.Text, StringComparison.Ordinal);
        Assert.Contains("1 open rooms", alert.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Seed.Alice.ToString(), alert.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scenario.Seed.Bob.ToString(), alert.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", alert.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExhaustedCloseRoundLeavesAnAlreadyResolvedRoomUntouched()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        Assert.Equal(ParticipationOutcome.Done, await scenario.Service.RespondAsync(scenario.Room, scenario.Seed.Alice, false, Ct));
        var resolved = await scenario.RoomSnapshotAsync();
        await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);

        await scenario.ExhaustAsync("retry", "close-round");

        Assert.Equal(resolved, await scenario.RoomSnapshotAsync());
        Assert.Contains("0 open rooms", Assert.Single(scenario.Logs.Entries, entry => entry.Level == LogLevel.Error).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkerThatLostTheJobCannotFailItNorReleaseReservations()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        await scenario.SetAttemptsAsync(jobId, 6);
        var stale = await scenario.Service.ClaimJobAsync(Ct);
        scenario.Clock.Advance(PastTheLease);
        var current = await scenario.Service.ClaimJobAsync(Ct);
        Assert.NotNull(stale);
        Assert.NotNull(current);

        await scenario.Service.RetryJobAsync(stale, Ct);

        Assert.Equal("InFlight|8|-", await scenario.JobAsync(jobId));
        Assert.Equal(2, await scenario.ActiveReservationsAsync());

        await scenario.Service.RetryJobAsync(current, Ct);

        Assert.Equal("Failed|8|ProcessingFailed", await scenario.JobAsync(jobId));
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
    }

    /// <summary>Marcar <c>Failed</c> e liberar são um commit só; enquanto não dá para liberar, a fila segue andando.</summary>
    [Fact]
    public async Task AClaimThatCannotReleaseLeavesTheJobOpenAndDoesNotHoldTheQueue()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        await scenario.SetAttemptsAsync(jobId, 8, inFlightWithExpiredLease: true);
        var expired = await scenario.JobAsync(jobId);
        await using var blocker = await scenario.Source.OpenConnectionAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from safety.user_guards where user_id=$1 for update", Ct, scenario.Seed.Alice);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => scenario.Service.ClaimJobAsync(Ct));

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, refused.SqlState);
        Assert.Equal(expired, await scenario.JobAsync(jobId));
        Assert.Equal(2, await scenario.ActiveReservationsAsync());
        var other = await scenario.AddDueJobAsync();
        var next = await scenario.Service.ClaimJobAsync(Ct);
        Assert.Equal(other, next?.Id);

        await hold.RollbackAsync(Ct);
        await scenario.FinishJobAsync(other);
        scenario.Clock.Advance(TimeSpan.FromSeconds(16));

        Assert.Null(await scenario.Service.ClaimJobAsync(Ct));
        Assert.Equal("Failed|9|LeaseExpiredExhausted", await scenario.JobAsync(jobId));
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(NpgsqlDataSource source, FakeTimeProvider clock, ParticipationService service, ParticipationSeed seed,
            Guid room, CapturingLoggerProvider logs)
        {
            Source = source;
            Clock = clock;
            Service = service;
            Seed = seed;
            Room = room;
            Logs = logs;
        }

        public NpgsqlDataSource Source { get; }

        public FakeTimeProvider Clock { get; }

        public ParticipationService Service { get; }

        public ParticipationSeed Seed { get; }

        public Guid Room { get; }

        public CapturingLoggerProvider Logs { get; }

        public Guid Round => Seed.Rounds[0];

        public static async Task<Scenario> CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
            var source = NpgsqlDataSource.Create(connectionString);
            var clock = new FakeTimeProvider(now);
            var logs = new CapturingLoggerProvider();
            using var loggers = new LoggerFactory([logs]);
            var service = new ParticipationService(source, clock, logger: loggers.CreateLogger<ParticipationService>());
            foreach (var user in new[] { seed.Alice, seed.Bob })
            {
                Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, false, Ct));
                Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(seed.Events[0], user, true, Ct));
            }

            clock.Advance(TimeSpan.FromMinutes(5));
            await service.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
            var room = (await service.StatusAsync(seed.Events[0], seed.Alice, Ct))!.RoomId!.Value;
            return new(source, clock, service, seed, room, logs);
        }

        public async Task AcceptBothAsync()
        {
            Assert.Equal(ParticipationOutcome.Done, await Service.RespondAsync(Room, Seed.Alice, true, Ct));
            Assert.Equal(ParticipationOutcome.Done, await Service.RespondAsync(Room, Seed.Bob, true, Ct));
        }

        /// <summary>Só este job fica disponível, para o claim não escolher outro; os demais continuam pendentes.</summary>
        public async Task<Guid> OnlyJobDueIsAsync(string kind, Guid aggregate)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await Execute(connection, """
                update ops.jobs set available_at=case when kind=$1 and aggregate_id=$2 then due_at else $3+interval '10 years' end
                where status='Pending'
                """, Ct, kind, aggregate, Clock.GetUtcNow());
            await using var query = Command(connection, "select id from ops.jobs where kind=$1 and aggregate_id=$2", kind, aggregate);
            return (Guid)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task ExhaustAsync(string path, string kind)
        {
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                var job = await Service.ClaimJobAsync(Ct);
                Assert.Equal(kind, job?.Kind);
                if (path == "retry")
                {
                    await Service.RetryJobAsync(job!, Ct);
                    Clock.Advance(PastTheRetryDelay);
                }
                else
                {
                    Clock.Advance(PastTheLease);
                }
            }

            if (path == "lease")
            {
                Assert.Null(await Service.ClaimJobAsync(Ct));
            }
        }

        public async Task SetAttemptsAsync(Guid jobId, int attempts, bool inFlightWithExpiredLease = false)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await Execute(connection, "update ops.jobs set attempts=$2 where id=$1", Ct, jobId, attempts);
            if (inFlightWithExpiredLease)
            {
                await Execute(connection, "update ops.jobs set status='InFlight',lease_expires_at=$2-interval '1 second' where id=$1", Ct, jobId, Clock.GetUtcNow());
            }
        }

        public async Task<Guid> AddDueJobAsync()
        {
            var id = Guid.CreateVersion7();
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'expire-invite',$2,$3,$3)", Ct, id, Guid.CreateVersion7(), Clock.GetUtcNow());
            return id;
        }

        public async Task FinishJobAsync(Guid jobId)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await Execute(connection, "update ops.jobs set status='Done',completed_at=$2,lease_expires_at=null where id=$1", Ct, jobId, Clock.GetUtcNow());
        }

        public Task<string> JobAsync(Guid jobId) => TextAsync(
            "select status||'|'||attempts||'|'||coalesce(last_error_code,'-') from ops.jobs where id=$1", jobId);

        public Task<string> RoomStateAsync() => TextAsync("select state from experiences.game_sessions where id=$1", Room);

        /// <summary>Estado, versão da sala e datas de liberação, para provar que nada mudou.</summary>
        public Task<string> RoomSnapshotAsync() => TextAsync("""
            select s.state||'|'||s.version||'|'||p.released_at::text||'|'||(select string_agg(pp.released_at::text,',') from events.pairing_participants pp where pp.pairing_id=p.id)
            from experiences.game_sessions s join events.pairings p on p.id=s.pairing_id where s.id=$1
            """, Room);

        public async Task<long> ActiveReservationsAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select count(*) from events.pairing_participants where pairing_id=(select pairing_id from experiences.game_sessions where id=$1) and released_at is null", Room);
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public ValueTask DisposeAsync() => Source.DisposeAsync();

        private async Task<string> TextAsync(string sql, params object[] values)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, sql, values);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }
    }
}
