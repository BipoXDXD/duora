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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegistrationBehindAHeldGuardAnswers503WithoutEffectAndSucceedsAfterRelease()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var path = $"/api/v1/events/{scene.EventId}/registration";

        await using (await scene.HoldGuardAsync(scene.AliceId))
        {
            using var busy = await scene.Alice.PutAsync(path).WaitAsync(RequestCeiling, Ct);

            await AssertUnavailableAsync(busy);
            Assert.Equal((0L, 0), await scene.RegistrationStateAsync());
        }

        using var retried = await scene.Alice.PutAsync(path).WaitAsync(RequestCeiling, Ct);

        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.Equal((1L, 1), await scene.RegistrationStateAsync());
    }

    [Theory]
    [InlineData("accept", "Invited", 1L)]
    [InlineData("reject", "Aborted", 0L)]
    [InlineData("exit", "Aborted", 0L)]
    public async Task RoomCommandBehindAHeldGuardAnswers503WithoutEffectAndSucceedsAfterRelease(string command, string stateAfterRelease, long acceptedAfterRelease)
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var room = await scene.OpenRoomAsync();
        var path = $"/api/v1/rooms/{room}/{command}";
        var before = await scene.RoomStateAsync(room);
        Assert.Equal(("Invited", 0L, 2L), before);

        await using (await scene.HoldGuardAsync(scene.AliceId))
        {
            using var busy = await scene.Alice.PostJsonAsync(path, null).WaitAsync(RequestCeiling, Ct);

            await AssertUnavailableAsync(busy);
            Assert.Equal(before, await scene.RoomStateAsync(room));
        }

        using var retried = await scene.Alice.PostJsonAsync(path, null).WaitAsync(RequestCeiling, Ct);

        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        var (state, accepted, _) = await scene.RoomStateAsync(room);
        Assert.Equal((stateAfterRelease, acceptedAfterRelease), (state, accepted));
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(RetryAfterSeconds, ProblemAssert.RetryAfterSeconds(response));
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

        public async Task<(long Registrations, int Confirmed)> RegistrationStateAsync()
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select (select count(*) from events.registrations where event_id=e.id and user_id=$2),e.confirmed_count from events.events e where e.id=$1", EventId, _seed.Alice);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetInt64(0), reader.GetInt32(1));
        }

        public async Task<(string State, long Accepted, long Reserved)> RoomStateAsync(Guid room)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select gs.state,
                  (select count(*) from experiences.game_participants gp where gp.game_session_id=gs.id and gp.accepted_at is not null),
                  (select count(*) from events.pairing_participants pp where pp.user_id = any($2) and pp.released_at is null)
                from experiences.game_sessions gs where gs.id=$1
                """, room, new[] { _seed.Alice, _seed.Bob });
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
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
