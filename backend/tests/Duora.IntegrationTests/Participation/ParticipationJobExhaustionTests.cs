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
        var alert = Assert.Single(scenario.Logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("could not release", alert.Text, StringComparison.Ordinal);
        Assert.Contains("close-round", alert.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(PostgresException), alert.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Seed.Alice.ToString(), alert.Text, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>No retry, marcar <c>Failed</c> e liberar também são um commit só: sem os locks, o job fica para o lease devolver.</summary>
    [Fact]
    public async Task ARetryThatCannotReleaseLeavesTheJobInFlightAndTheReservationsHeld()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        await scenario.SetAttemptsAsync(jobId, 7);
        var job = await scenario.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        await using var blocker = await scenario.Source.OpenConnectionAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from safety.user_guards where user_id=$1 for update", Ct, scenario.Seed.Alice);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => scenario.Service.RetryJobAsync(job, Ct));

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, refused.SqlState);
        Assert.Equal("InFlight|8|-", await scenario.JobAsync(jobId));
        Assert.Equal(2, await scenario.ActiveReservationsAsync());

        await hold.RollbackAsync(Ct);
        scenario.Clock.Advance(PastTheLease);

        Assert.Null(await scenario.Service.ClaimJobAsync(Ct));
        Assert.Equal("Failed|9|LeaseExpiredExhausted", await scenario.JobAsync(jobId));
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
    }

    [Fact]
    public async Task AnExhaustedCloseRoundDoesNotTouchAnotherRoundThatIsAlsoPastItsDeadline()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        var otherRoom = await scenario.OpenRoomInAnotherRoundAsync();
        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        var untouched = await scenario.OpenRoomStateAsync(otherRoom);
        Assert.Equal("Invited|1|2", untouched);

        await scenario.ExhaustAsync("retry", "close-round");

        Assert.StartsWith("Failed|", await scenario.JobAsync(jobId), StringComparison.Ordinal);
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
        Assert.Equal(untouched, await scenario.OpenRoomStateAsync(otherRoom));
    }

    [Theory]
    [InlineData("retry", false, "Closed|Completed")]
    [InlineData("lease", false, "Closed|Completed")]
    [InlineData("retry", true, "Closed|Running")]
    public async Task AnExhaustedCloseRoundClosesItsRoundAndCompletesTheEventOnlyAfterTheLastRound(string path, bool anotherRoundIsOpen, string expected)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        if (anotherRoundIsOpen)
        {
            await scenario.AddScheduledRoundAsync();
        }

        await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);

        await scenario.ExhaustAsync(path, "close-round");

        Assert.Equal(expected, await scenario.RoundAndEventStateAsync());
    }

    /// <summary>
    /// A falha vem do último passo, concluir o evento, depois de salas, reservas e rodada: tudo volta atrás junto com o
    /// <c>Failed</c>, e o job termina quando o passo voltar a funcionar.
    /// </summary>
    [Theory]
    [InlineData("retry")]
    [InlineData("lease")]
    public async Task AFailureAfterTheReleaseRollsBackTheFailedStateAndEverythingElse(string path)
    {
        await using var scenario = await Scenario.CreateAsync(postgres, scratchDatabase: true);
        var jobId = await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        scenario.Clock.Advance(PastTheRoundDeadline);
        await scenario.RefuseEventCompletionAsync();
        ParticipationJob? claimed = null;
        if (path == "retry")
        {
            await scenario.SetAttemptsAsync(jobId, 7);
            claimed = await scenario.Service.ClaimJobAsync(Ct);
            Assert.NotNull(claimed);
        }
        else
        {
            await scenario.SetAttemptsAsync(jobId, 8, inFlightWithExpiredLease: true);
        }

        var refused = await Assert.ThrowsAsync<PostgresException>(() => claimed is null
            ? scenario.Service.ClaimJobAsync(Ct)
            : scenario.Service.RetryJobAsync(claimed, Ct));

        Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
        Assert.Equal("InFlight|8|-", await scenario.JobAsync(jobId));
        Assert.Equal(2, await scenario.ActiveReservationsAsync());
        Assert.Equal("Invited", await scenario.RoomStateAsync());
        Assert.Equal("Inviting|Running", await scenario.RoundAndEventStateAsync());

        await scenario.AllowEventCompletionAsync();
        scenario.Clock.Advance(PastTheLease);

        Assert.Null(await scenario.Service.ClaimJobAsync(Ct));
        Assert.Equal("Failed|9|LeaseExpiredExhausted", await scenario.JobAsync(jobId));
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
        Assert.Equal("Closed|Completed", await scenario.RoundAndEventStateAsync());
    }

    /// <summary>
    /// Mesma regra do fechamento normal: só rodada em andamento fecha, a que não começou é cancelada e a terminada não muda.
    /// A sala aberta é liberada em todos os casos, inclusive com a rodada já <c>Cancelled</c>: o estado da rodada não pode
    /// segurar as reservas, porque este é o último job que as libera.
    /// </summary>
    [Theory]
    [InlineData("Inviting", "Closed", 1)]
    [InlineData("Scheduled", "Cancelled", 1)]
    [InlineData("Cancelled", "Cancelled", 0)]
    [InlineData("Closed", "Closed", 0)]
    public async Task AnExhaustedCloseRoundFollowsTheRoundCloseRule(string initial, string expected, int versionIncrease)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        await scenario.OnlyJobDueIsAsync("close-round", scenario.Round);
        var version = await scenario.SetRoundStateAsync(initial);
        scenario.Clock.Advance(PastTheRoundDeadline);

        await scenario.ExhaustAsync("retry", "close-round");

        Assert.Equal($"{expected}|Completed", await scenario.RoundAndEventStateAsync());
        Assert.Equal(version + versionIncrease, await scenario.RoundVersionAsync());
        Assert.Equal(0, await scenario.ActiveReservationsAsync());
        Assert.Equal("Expired", await scenario.RoomStateAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly ScratchDatabase? _scratch;

        private Scenario(NpgsqlDataSource source, FakeTimeProvider clock, ParticipationService service, ParticipationSeed seed,
            Guid room, CapturingLoggerProvider logs, string connectionString, ScratchDatabase? scratch)
        {
            _scratch = scratch;
            ConnectionString = connectionString;
            Source = source;
            Clock = clock;
            Service = service;
            Seed = seed;
            Room = room;
            Logs = logs;
        }

        public string ConnectionString { get; }

        public NpgsqlDataSource Source { get; }

        public FakeTimeProvider Clock { get; }

        public ParticipationService Service { get; }

        public ParticipationSeed Seed { get; }

        public Guid Room { get; }

        public CapturingLoggerProvider Logs { get; }

        public Guid Round => Seed.Rounds[0];

        /// <param name="postgres">O PostgreSQL dos testes.</param>
        /// <param name="scratchDatabase">Banco com acesso de migrador, para o teste instalar um gatilho de falha.</param>
        public static async Task<Scenario> CreateAsync(PostgresFixture postgres, bool scratchDatabase = false)
        {
            var scratch = scratchDatabase ? await postgres.CreateMigratedScratchDatabaseAsync(Ct) : null;
            var connectionString = scratch?.AppConnectionString ?? await postgres.CreateMigratedDatabaseAsync(Ct);
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
            return new(source, clock, service, seed, room, logs, connectionString, scratch);
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

        /// <summary>Outra dupla, em outro evento: sala aberta numa rodada independente da que o teste esgota.</summary>
        public async Task<Guid> OpenRoomInAnotherRoundAsync()
        {
            var other = await BrowserScenarioSeed.InitializeAsync(ConnectionString, Clock.GetUtcNow(), Ct, "-outra", declarePreferences: true);
            foreach (var user in new[] { other.Alice, other.Bob })
            {
                Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(other.Events[0], user, false, Ct));
                Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(other.Events[0], user, true, Ct));
            }

            Clock.Advance(TimeSpan.FromMinutes(5));
            await Service.BuildRoundPairingsAsync(other.Rounds[0], Ct);
            return (await Service.StatusAsync(other.Events[0], other.Alice, Ct))!.RoomId!.Value;
        }

        public async Task AddScheduledRoundAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            var start = Clock.GetUtcNow().AddHours(1);
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,2,'Scheduled',$3,$4,42)", Ct, Guid.NewGuid(), Seed.Events[0], start, start.AddMinutes(12));
        }

        /// <summary><c>estado|versão|reservas ativas</c>.</summary>
        public Task<string> OpenRoomStateAsync(Guid room) => TextAsync("""
            select s.state||'|'||s.version||'|'||(select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null)
            from experiences.game_sessions s where s.id=$1
            """, room);

        public Task<string> RoundAndEventStateAsync() => TextAsync("""
            select r.state||'|'||e.state from events.rounds r join events.events e on e.id=r.event_id where r.id=$1
            """, Round);

        public Task RefuseEventCompletionAsync() => ExecuteAsMigratorAsync("""
            create function events.refuse_completion_in_test() returns trigger language plpgsql as $$
            begin raise exception 'forced by the test'; end $$;
            create trigger refuse_completion_in_test before update on events.events for each row
              when (new.state='Completed') execute function events.refuse_completion_in_test();
            """);

        public Task AllowEventCompletionAsync() => ExecuteAsMigratorAsync(
            "drop trigger refuse_completion_in_test on events.events; drop function events.refuse_completion_in_test();");

        public async Task<long> SetRoundStateAsync(string state)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "update events.rounds set state=$2 where id=$1 returning version", Round, state);
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task<long> RoundVersionAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select version from events.rounds where id=$1", Round);
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        private async Task ExecuteAsMigratorAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_scratch!.MigratorConnectionString);
            await connection.OpenAsync(Ct);
            await Execute(connection, sql, Ct);
        }

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

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            if (_scratch is not null)
            {
                await _scratch.DisposeAsync();
            }
        }

        private async Task<string> TextAsync(string sql, params object[] values)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, sql, values);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }
    }
}
