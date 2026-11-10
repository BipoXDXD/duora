using System.Diagnostics;
using System.Net;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Outra transação segurando a guarda da pessoa: passado o <c>lock_timeout</c>, a rota responde 503 com Retry-After,
/// não grava nada e a mesma chamada funciona depois que a guarda é solta.
/// </summary>
public sealed class ParticipationLockContentionTests(PostgresFixture postgres)
{
    private const int RetryAfterSeconds = 2;

    /// <summary>Bem acima dos 2 s do <c>lock_timeout</c>: passar daqui é requisição presa, não contenção respondida.</summary>
    private static readonly TimeSpan RequestCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// O Postgres só desiste depois de esperar o <c>lock_timeout</c> inteiro, então carga na máquina não faz este piso
    /// falhar; a folga cobre a diferença entre o relógio do host e o do container.
    /// </summary>
    private static readonly TimeSpan LockWaitFloor = TimeSpan.FromMilliseconds(1900);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegistrationBehindAHeldGuardAnswers503WithoutEffectAndSucceedsAfterRelease()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var path = $"/api/v1/events/{scene.EventId}/registration";
        var before = await scene.RegistrationStateAsync();
        Assert.Equal((0L, 0, 1L, 1L), before.Columns);

        await using (await scene.HoldGuardAsync(scene.AliceId))
        {
            await AssertUnavailableAfterLockWaitAsync(() => scene.Alice.PutAsync(path));

            Assert.Equal(before, await scene.RegistrationStateAsync());
        }

        using var retried = await scene.Alice.PutAsync(path).WaitAsync(RequestCeiling, Ct);

        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        var after = await scene.RegistrationStateAsync();
        Assert.Equal((1L, 1, 1L, 2L), after.Columns);
        Assert.NotEqual(before.Rows, after.Rows);
    }

    [Theory]
    [InlineData("accept", "Invited", 1L, 2L, 0L)]
    [InlineData("reject", "Aborted", 0L, 0L, 0L)]
    [InlineData("exit", "Aborted", 0L, 0L, 1L)]
    public async Task RoomCommandBehindAHeldGuardAnswers503WithoutEffectAndSucceedsAfterRelease(string command, string stateAfterRelease, long acceptedAfterRelease, long reservedAfterRelease, long departedAfterRelease)
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var room = await scene.OpenRoomAsync();
        var path = $"/api/v1/rooms/{room}/{command}";
        var before = await scene.RoomStateAsync(room);
        Assert.Equal(("Invited", 0L, 2L, 0L, 1L), before.Columns);

        await using (await scene.HoldGuardAsync(scene.AliceId))
        {
            await AssertUnavailableAfterLockWaitAsync(() => scene.Alice.PostJsonAsync(path, null));

            Assert.Equal(before, await scene.RoomStateAsync(room));
        }

        using var retried = await scene.Alice.PostJsonAsync(path, null).WaitAsync(RequestCeiling, Ct);

        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        var after = await scene.RoomStateAsync(room);
        Assert.Equal((stateAfterRelease, acceptedAfterRelease, reservedAfterRelease, departedAfterRelease, 2L), after.Columns);
        Assert.NotEqual(before.Rows, after.Rows);
    }

    private static async Task AssertUnavailableAfterLockWaitAsync(Func<Task<HttpResponseMessage>> send)
    {
        var waiting = Stopwatch.StartNew();
        using var response = await send().WaitAsync(RequestCeiling, Ct);
        var waited = waiting.Elapsed;

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(RetryAfterSeconds, ProblemAssert.RetryAfterSeconds(response));
        Assert.True(waited >= LockWaitFloor, $"A rota desistiu da guarda em {waited.TotalMilliseconds} ms.");
    }

    /// <summary>As colunas com valor conhecido e, em <paramref name="Rows"/>, as linhas inteiras que a rota de inscrição escreve.</summary>
    private sealed record RegistrationState(long Registrations, int Confirmed, long NextWaitlistPosition, long Version, string Rows)
    {
        public (long, int, long, long) Columns => (Registrations, Confirmed, NextWaitlistPosition, Version);
    }

    /// <summary>As colunas com valor conhecido e, em <paramref name="Rows"/>, as linhas inteiras que os comandos de sala escrevem.</summary>
    private sealed record RoomState(string State, long Accepted, long Reserved, long Departed, long Version, string Rows)
    {
        public (string, long, long, long, long) Columns => (State, Accepted, Reserved, Departed, Version);
    }

    private sealed class Scene : IAsyncDisposable
    {
        private readonly DuoraWebFactory _factory;
        private readonly NpgsqlDataSource _source;
        private readonly FakeTimeProvider _clock;
        private readonly ParticipationSeed _seed;

        private Scene(DuoraWebFactory factory, NpgsqlDataSource source, FakeTimeProvider clock, ParticipationSeed seed, Browser alice)
        {
            _factory = factory;
            _source = source;
            _clock = clock;
            _seed = seed;
            Alice = alice;
        }

        public Browser Alice { get; }

        public Guid AliceId => _seed.Alice;

        public Guid EventId => _seed.Events[0];

        public static async Task<Scene> CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, clock.GetUtcNow(), Ct, declarePreferences: true);
            var factory = new DuoraWebFactory(connectionString) { Time = clock };
            var identity = new IdentityScenario(connectionString, factory);
            var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
            return new(factory, NpgsqlDataSource.Create(connectionString), clock, seed, alice);
        }

        public async Task<Guid> OpenRoomAsync()
        {
            var service = new ParticipationService(_source, _clock);
            foreach (var user in new[] { _seed.Alice, _seed.Bob })
            {
                Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(EventId, user, false, Ct));
                Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(EventId, user, true, Ct));
            }

            _clock.Advance(TimeSpan.FromMinutes(5));
            await service.BuildRoundPairingsAsync(_seed.Rounds[0], Ct);
            return Assert.IsType<Guid>((await service.StatusAsync(EventId, _seed.Alice, Ct))!.RoomId);
        }

        /// <summary>Segura a guarda até o descarte, sem esperar: quem pedir a mesma linha fica até o <c>lock_timeout</c>.</summary>
        public async Task<IAsyncDisposable> HoldGuardAsync(Guid user)
        {
            var connection = await _source.OpenConnectionAsync(Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await Execute(connection, "insert into safety.user_guards(user_id) values($1) on conflict do nothing", Ct, user);
            await Execute(connection, "select 1 from safety.user_guards where user_id=$1 for update", Ct, user);
            return new HeldGuard(connection, transaction);
        }

        public async Task<RegistrationState> RegistrationStateAsync()
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select (select count(*) from events.registrations where event_id=e.id and user_id=$2),
                  e.confirmed_count, e.next_waitlist_position, e.version,
                  jsonb_build_object('event', to_jsonb(e),
                    'registrations', (select jsonb_agg(to_jsonb(r) order by r.user_id) from events.registrations r where r.event_id=e.id))::text
                from events.events e where e.id=$1
                """, EventId, _seed.Alice);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
        }

        public async Task<RoomState> RoomStateAsync(Guid room)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select gs.state,
                  (select count(*) from experiences.game_participants gp where gp.game_session_id=gs.id and gp.accepted_at is not null),
                  (select count(*) from events.pairing_participants pp where pp.user_id = any($2) and pp.released_at is null),
                  (select count(*) from experiences.game_participants gp where gp.game_session_id=gs.id and gp.departed_at is not null),
                  gs.version,
                  jsonb_build_object('session', to_jsonb(gs),
                    'participants', (select jsonb_agg(to_jsonb(gp) order by gp.user_id) from experiences.game_participants gp where gp.game_session_id=gs.id),
                    'pairing', (select to_jsonb(p) from events.pairings p where p.id=gs.pairing_id),
                    'reservations', (select jsonb_agg(to_jsonb(pp) order by pp.user_id) from events.pairing_participants pp where pp.pairing_id=gs.pairing_id),
                    'actions', (select jsonb_agg(to_jsonb(a) order by a.action_id) from experiences.game_actions a where a.game_session_id=gs.id))::text
                from experiences.game_sessions gs where gs.id=$1
                """, room, new[] { _seed.Alice, _seed.Bob });
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5));
        }

        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            await _source.DisposeAsync();
            await _factory.DisposeAsync();
        }

        private sealed class HeldGuard(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await transaction.RollbackAsync(Ct);
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
