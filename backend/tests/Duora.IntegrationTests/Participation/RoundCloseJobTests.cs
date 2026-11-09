using Duora.Infrastructure.Participation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O job <c>close-round</c> com dados reais, pelas duas chamadas que o <c>ParticipationJobService</c> faz em produção:
/// <c>ClaimJobAsync</c> e <c>CompleteJobAsync</c>. Cada sala aberta tem o próprio job de expiração, que normalmente
/// roda antes; o cenário os deixa em <c>Failed</c>, o estado em que eles ficam depois de esgotar as tentativas, porque
/// é aí que o fechamento da rodada é a última chance de liberar as reservas.
/// </summary>
public sealed class RoundCloseJobTests(PostgresFixture postgres)
{
    private const int MaximumDueJobs = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CloseRoundExpiresOpenRoomsAndReleasesReservationsOnce()
    {
        await using var round = await OpenRoundAsync();
        var invited = await round.RoomAsync(round.InvitedRoom);
        var active = await round.RoomAsync(round.ActiveRoom);
        var closing = await round.RoundRowAsync();
        var running = await round.EventRowAsync();
        Assert.Equal("Invited", invited.State);
        Assert.Equal("Active", active.State);
        Assert.Equal(4L, await round.ActiveReservationsAsync());

        round.Clock.SetUtcNow(round.Deadline);
        var completed = await RunDueJobsAsync(round.Service);

        Assert.Contains(completed, job => job.Kind == "close-round" && job.AggregateId == round.Round);
        Assert.Equal(("Expired", invited.Version + 1), await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(("Expired", active.Version + 1), await round.RoomAsync(round.ActiveRoom));
        Assert.Equal(0L, await round.ActiveReservationsAsync());
        Assert.Equal(("Closed", closing.Version + 1), await round.RoundRowAsync());
        Assert.Equal(("Completed", running.Version + 1), await round.EventRowAsync());

        Assert.DoesNotContain(await RunDueJobsAsync(round.Service), job => job.AggregateId == round.Round);
        Assert.Equal(("Expired", invited.Version + 1), await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(("Expired", active.Version + 1), await round.RoomAsync(round.ActiveRoom));
        Assert.Equal(("Closed", closing.Version + 1), await round.RoundRowAsync());
        Assert.Equal(("Completed", running.Version + 1), await round.EventRowAsync());
    }

    [Fact]
    public async Task AWorkerWhoseLeaseExpiredCannotCloseTheRoundAndTheNewOwnerClosesItOnce()
    {
        await using var round = await OpenRoundAsync();
        var invited = await round.RoomAsync(round.InvitedRoom);
        var closing = await round.RoundRowAsync();
        var running = await round.EventRowAsync();
        round.Clock.SetUtcNow(round.Deadline);
        var expired = await ClaimCloseJobAsync(round);
        round.Clock.Advance(TimeSpan.FromSeconds(61));
        var owner = await ClaimCloseJobAsync(round);
        Assert.True(owner.Token > expired.Token);

        Assert.False(await round.Service.CompleteJobAsync(expired, Ct));

        Assert.Equal(invited, await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(closing, await round.RoundRowAsync());
        Assert.Equal(4L, await round.ActiveReservationsAsync());

        Assert.True(await round.Service.CompleteJobAsync(owner, Ct));
        Assert.False(await round.Service.CompleteJobAsync(expired, Ct));

        Assert.Equal(("Expired", invited.Version + 1), await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(("Closed", closing.Version + 1), await round.RoundRowAsync());
        Assert.Equal(("Completed", running.Version + 1), await round.EventRowAsync());
        Assert.Equal(0L, await round.ActiveReservationsAsync());
    }

    [Fact]
    public async Task CloseRoundIsNotDeliveredOneTickBeforeTheDeadlineAndIsDeliveredAtIt()
    {
        await using var round = await OpenRoundAsync();
        var invited = await round.RoomAsync(round.InvitedRoom);
        var active = await round.RoomAsync(round.ActiveRoom);
        var inviting = await round.RoundRowAsync();

        round.Clock.SetUtcNow(round.Deadline.AddTicks(-1));
        var early = await RunDueJobsAsync(round.Service);

        Assert.DoesNotContain(early, job => job.AggregateId == round.Round && job.Kind == "close-round");
        Assert.Equal(("Pending", 0), await round.CloseJobRowAsync());
        Assert.Equal(invited, await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(active, await round.RoomAsync(round.ActiveRoom));
        Assert.Equal(inviting, await round.RoundRowAsync());
        Assert.Equal(4L, await round.ActiveReservationsAsync());

        round.Clock.SetUtcNow(round.Deadline);

        Assert.Contains(await RunDueJobsAsync(round.Service), job => job.AggregateId == round.Round && job.Kind == "close-round");
        Assert.Equal(("Expired", invited.Version + 1), await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(("Closed", inviting.Version + 1), await round.RoundRowAsync());
        Assert.Equal(0L, await round.ActiveReservationsAsync());
    }

    /// <summary>A fila não entrega antes do prazo; aqui o job é adiantado à mão para exercitar a segunda barreira, a do próprio fechamento.</summary>
    [Fact]
    public async Task ACloseRoundJobForcedBeforeTheDeadlineLeavesRoomsAndRoundUntouched()
    {
        await using var round = await OpenRoundAsync();
        var invited = await round.RoomAsync(round.InvitedRoom);
        var active = await round.RoomAsync(round.ActiveRoom);
        var inviting = await round.RoundRowAsync();
        var running = await round.EventRowAsync();
        await round.ExecuteAsync("update ops.jobs set available_at=$2 where kind='close-round' and aggregate_id=$1", round.Round, round.Deadline.AddMinutes(-1));
        round.Clock.SetUtcNow(round.Deadline.AddTicks(-1));

        await RunDueJobsAsync(round.Service);

        Assert.Equal(invited, await round.RoomAsync(round.InvitedRoom));
        Assert.Equal(active, await round.RoomAsync(round.ActiveRoom));
        Assert.Equal(inviting, await round.RoundRowAsync());
        Assert.Equal(running, await round.EventRowAsync());
        Assert.Equal(4L, await round.ActiveReservationsAsync());
    }

    [Fact]
    public async Task TheEventCompletesOnlyWhenItsLastRoundCloses()
    {
        await using var round = await OpenRoundAsync();
        var running = await round.EventRowAsync();
        var lastRound = Guid.NewGuid();
        var lastDeadline = round.Deadline.AddMinutes(12);
        await round.ExecuteAsync("insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values($1,$2,2,'Inviting',$3,$4,42)",
            lastRound, round.EventId, round.Deadline, lastDeadline);
        await round.ExecuteAsync("insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,'close-round',$2,$3,$3)", Guid.NewGuid(), lastRound, lastDeadline);

        round.Clock.SetUtcNow(round.Deadline);
        await RunDueJobsAsync(round.Service);

        Assert.Equal("Closed", (await round.RoundRowAsync()).State);
        Assert.Equal("Inviting", (await round.RoundRowAsync(lastRound)).State);
        Assert.Equal(running, await round.EventRowAsync());

        round.Clock.SetUtcNow(lastDeadline);
        await RunDueJobsAsync(round.Service);

        Assert.Equal("Closed", (await round.RoundRowAsync(lastRound)).State);
        Assert.Equal(("Completed", running.Version + 1), await round.EventRowAsync());
    }

    private async Task<OpenRound> OpenRoundAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var others = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, "-outros", true);
        var source = NpgsqlDataSource.Create(connectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        var eventId = seed.Events[0];
        var roundId = seed.Rounds[0];
        Guid[] users = [seed.Alice, seed.Bob, others.Alice, others.Bob];
        foreach (var user in users)
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, Ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await RunDueJobsAsync(service);
        var round = new OpenRound(source, clock, service, eventId, roundId, users);
        await round.BindRoomsAsync();
        return round;
    }

    private static async Task<IReadOnlyList<ParticipationJob>> RunDueJobsAsync(ParticipationService service)
    {
        List<ParticipationJob> completed = [];
        for (var attempt = 0; attempt < MaximumDueJobs; attempt++)
        {
            var job = await service.ClaimJobAsync(Ct);
            if (job is null)
            {
                return completed;
            }

            Assert.True(await service.CompleteJobAsync(job, Ct), $"O job {job.Kind} recém-entregue não foi concluído.");
            completed.Add(job);
        }

        throw new InvalidOperationException("A fila de jobs não esvaziou.");
    }

    /// <summary>Conclui o que estiver na frente na fila, como o worker faria, até receber o <c>close-round</c> da rodada.</summary>
    private static async Task<ParticipationJob> ClaimCloseJobAsync(OpenRound round)
    {
        for (var attempt = 0; attempt < MaximumDueJobs; attempt++)
        {
            var job = await round.Service.ClaimJobAsync(Ct);
            Assert.NotNull(job);
            if (job.Kind == "close-round" && job.AggregateId == round.Round)
            {
                return job;
            }

            Assert.True(await round.Service.CompleteJobAsync(job, Ct));
        }

        throw new InvalidOperationException("O close-round da rodada não foi entregue.");
    }

    private sealed class OpenRound(NpgsqlDataSource source, FakeTimeProvider clock, ParticipationService service, Guid eventId, Guid round, Guid[] users) : IAsyncDisposable
    {
        public FakeTimeProvider Clock => clock;

        public ParticipationService Service => service;

        public Guid EventId => eventId;

        public Guid Round => round;

        public Guid InvitedRoom { get; private set; }

        public Guid ActiveRoom { get; private set; }

        public DateTimeOffset Deadline { get; private set; }

        public async Task BindRoomsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            List<(Guid Room, Guid Low, Guid High)> rooms = [];
            await using (var query = Command(connection, "select s.id,p.user_low,p.user_high from experiences.game_sessions s join events.pairings p on p.id=s.pairing_id where p.round_id=$1 order by s.id", round))
            await using (var reader = await query.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    rooms.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
                }
            }

            Assert.Equal(2, rooms.Count);
            InvitedRoom = rooms[0].Room;
            ActiveRoom = rooms[1].Room;
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(ActiveRoom, rooms[1].Low, true, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(ActiveRoom, rooms[1].High, true, Ct));
            const int ExpiryJobsOfBothRooms = 3; // expire-invite das duas salas e game-deadline da sala ativa.
            Assert.Equal(ExpiryJobsOfBothRooms, await Execute(connection, """
                update ops.jobs set status='Failed',attempts=8,completed_at=$2,last_error_code='ProcessingFailed'
                where kind in ('expire-invite','game-deadline') and aggregate_id=any($1) and status='Pending'
                """, Ct, new[] { InvitedRoom, ActiveRoom }, clock.GetUtcNow()));
            await using var deadline = Command(connection, "select deadline_at from events.rounds where id=$1", round);
            Deadline = new DateTimeOffset((DateTime)(await deadline.ExecuteScalarAsync(Ct))!);
        }

        public Task<(string State, long Version)> RoomAsync(Guid room) => StateAsync("select state,version from experiences.game_sessions where id=$1", room);

        public Task<(string State, long Version)> RoundRowAsync() => RoundRowAsync(round);

        public Task<(string State, long Version)> RoundRowAsync(Guid id) => StateAsync("select state,version from events.rounds where id=$1", id);

        public Task<(string State, long Version)> EventRowAsync() => StateAsync("select state,version from events.events where id=$1", eventId);

        public async Task<(string Status, int Attempts)> CloseJobRowAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select status,attempts from ops.jobs where kind='close-round' and aggregate_id=$1", round);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt32(1));
        }

        public async Task<long> ActiveReservationsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select count(*) from events.pairing_participants where user_id=any($1) and released_at is null", users);
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            Assert.Equal(1, await Execute(connection, sql, Ct, values));
        }

        public ValueTask DisposeAsync() => source.DisposeAsync();

        private async Task<(string State, long Version)> StateAsync(string sql, Guid id)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, sql, id);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt64(1));
        }
    }
}
