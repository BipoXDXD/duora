using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Estado de uma sala lido direto do PostgreSQL, para comparar antes e depois de uma operação recusada.</summary>
internal sealed record RoomFacts(
    string State,
    long Version,
    DateTimeOffset? AliceAcceptedAt,
    DateTimeOffset? BobAcceptedAt,
    long ActiveReservations,
    long Actions);

/// <summary>
/// Cenário sintético de participação com relógio falso: a seed do navegador, o serviço real e, sob demanda, a fábrica
/// web com navegadores logados. Serve às classes de teste que precisam de uma sala convidada ou de inscrições.
/// </summary>
internal sealed class ParticipationScene : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _connectionString;
    private DuoraWebFactory? _factory;

    private ParticipationScene(string connectionString, NpgsqlDataSource source, FakeTimeProvider clock, ParticipationService service, ParticipationSeed seed)
    {
        _connectionString = connectionString;
        Source = source;
        Clock = clock;
        Service = service;
        Seed = seed;
    }

    public NpgsqlDataSource Source { get; }

    public FakeTimeProvider Clock { get; }

    public ParticipationService Service { get; }

    public ParticipationSeed Seed { get; }

    /// <summary>A sala criada por <see cref="InviteAliceAndBobAsync"/>.</summary>
    public Guid Room { get; private set; }

    public static async Task<ParticipationScene> CreateAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var source = NpgsqlDataSource.Create(connectionString);
        return new(connectionString, source, clock, new ParticipationService(source, clock), seed);
    }

    /// <summary>Inscreve e faz check-in de Alice e Bob no primeiro evento, constrói a rodada e guarda a sala <c>Invited</c>.</summary>
    public async Task InviteAliceAndBobAsync()
    {
        var eventId = Seed.Events[0];
        foreach (var user in new[] { Seed.Alice, Seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(eventId, user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await Service.RegisterAsync(eventId, user, true, Ct));
        }

        Clock.Advance(TimeSpan.FromMinutes(5));
        await Service.BuildRoundPairingsAsync(Seed.Rounds[0], Ct);
        Room = (await Service.StatusAsync(eventId, Seed.Alice, Ct))!.RoomId!.Value;
    }

    public async Task AcceptBothAsync()
    {
        Assert.Equal(ParticipationOutcome.Done, await Service.RespondAsync(Room, Seed.Alice, true, Ct));
        Assert.Equal(ParticipationOutcome.Done, await Service.RespondAsync(Room, Seed.Bob, true, Ct));
    }

    /// <summary>As duas escolhas que fecham o jogo da constelação com a sala <c>Completed</c>.</summary>
    public async Task CompleteTheGameAsync()
    {
        var version = (await Service.RoomAsync(Room, Seed.Alice, Ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await Service.ActionAsync(Room, Seed.Alice, Guid.NewGuid(), version, "choose-option", "morning", "tea", Ct));
        version = (await Service.RoomAsync(Room, Seed.Bob, Ct))!.Version;
        Assert.Equal(ParticipationOutcome.Done, await Service.ActionAsync(Room, Seed.Bob, Guid.NewGuid(), version, "choose-option", "morning", "walk", Ct));
    }

    public async Task<Browser> SignInAsync(Guid user)
    {
        _factory ??= new DuoraWebFactory(_connectionString) { Time = Clock };
        var email = user == Seed.Alice ? "alice@example.com" : user == Seed.Bob ? "bob@example.com" : "third@example.com";
        return await new IdentityScenario(_connectionString, _factory).SignedInBrowserAsync(new(user, email, IdentityScenario.ValidPassword));
    }

    public Task<DateTimeOffset> InviteDeadlineAsync() =>
        ReadInstantAsync("select invite_expires_at from experiences.game_sessions where id=$1");

    public Task<DateTimeOffset> GameDeadlineAsync() =>
        ReadInstantAsync("select active_deadline_at from experiences.game_sessions where id=$1");

    public async Task<RoomFacts> RoomFactsAsync()
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, """
            select s.state,s.version,
              (select gp.accepted_at from experiences.game_participants gp where gp.game_session_id=s.id and gp.user_id=$2),
              (select gp.accepted_at from experiences.game_participants gp where gp.game_session_id=s.id and gp.user_id=$3),
              (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
              (select count(*) from experiences.game_actions a where a.game_session_id=s.id)
            from experiences.game_sessions s where s.id=$1
            """, Room, Seed.Alice, Seed.Bob);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new(reader.GetString(0), reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt64(4), reader.GetInt64(5));
    }

    /// <summary>Adia os outros jobs pendentes, para o claim só poder escolher o job <paramref name="kind"/> da sala.</summary>
    public async Task IsolateJobAsync(string kind)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await Execute(connection, """
            update ops.jobs set available_at=$3+interval '10 years'
            where status='Pending' and not (kind=$2 and aggregate_id=$1)
            """, Ct, Room, kind, Clock.GetUtcNow());
    }

    public async Task<ParticipationJob> ClaimAsync(string kind)
    {
        var job = await Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.Equal((kind, Room), (job.Kind, job.AggregateId));
        return job;
    }

    /// <summary><c>status|tentativas</c> do job <paramref name="kind"/> da sala.</summary>
    public async Task<string> JobAsync(string kind)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select status||'|'||attempts from ops.jobs where kind=$2 and aggregate_id=$1", Room, kind);
        return (string)(await query.ExecuteScalarAsync(Ct))!;
    }

    public async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await Execute(connection, sql, Ct, values);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Source.DisposeAsync();
    }

    private async Task<DateTimeOffset> ReadInstantAsync(string sql)
    {
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, sql, Room);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }
}
