using System.Globalization;
using System.Net;
using System.Text.Json;
using Duora.Contracts.Participation;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O prazo da sala ativa (T-18, INV-08) no PostgreSQL real, com relógio falso, pelas duas chamadas que o worker faz em
/// produção: <c>ClaimJobAsync</c> e <c>CompleteJobAsync</c> do job <c>game-deadline</c>, e a ação HTTP que chega entre o
/// prazo e o job.
/// </summary>
public sealed class GameDeadlineTests(PostgresFixture postgres)
{
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheGameDeadlineJobIsNotDeliveredOneTickBeforeTheDeadlineAndExpiresTheRoomAtIt()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        Assert.Equal(("Active", 2L, 0L), (active.State, active.ActiveReservations, active.Actions));

        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        Assert.Null(await scene.Service.ClaimJobAsync(Ct));
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());
        Assert.Equal(active, await scene.RoomFactsAsync());

        scene.Clock.SetUtcNow(scene.Deadline);
        var job = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        Assert.Equal(new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0), await scene.RoomFactsAsync());
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());
    }

    /// <summary>
    /// A fila não entrega antes do prazo; aqui o job é adiantado à mão para exercitar a segunda barreira, a do próprio motor.
    /// Lacuna registrada na T-18: se a fila entregar cedo, o job é consumido (<c>Done</c>) sem expirar a sala, não é
    /// reagendado, e só o <c>close-round</c> encerra a sala depois. O assert do job fixa esse comportamento atual.
    /// </summary>
    [Fact]
    public async Task AGameDeadlineJobForcedOneTickBeforeTheDeadlineLeavesTheRoomActive()
    {
        await using var scene = await ActiveRoomAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        await scene.AnticipateGameDeadlineJobAsync();

        var job = await scene.Service.ClaimJobAsync(Ct);

        Assert.NotNull(job);
        Assert.Equal(("game-deadline", scene.Room), (job.Kind, job.AggregateId));
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
        Assert.Equal("Done|1", await scene.GameDeadlineJobAsync());
    }

    /// <summary>
    /// Entre o prazo e o job a sala segue <c>Active</c> no banco. Quem decide é o relógio do servidor (spec 10.4, "Timeout
    /// vs ação"; T-18): a ação é recusada sem gravar nada e o job, quando roda, ainda expira a sala. Uma ação nova recebe,
    /// antes e depois do job, a mesma <c>404</c> de uma sala inexistente, em status, corpo sem <c>traceId</c> e headers. A
    /// leitura da sala continua mostrando o estado do job (<c>Active</c> antes, <c>Expired</c> depois).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task AnActionAtOrAfterTheDeadlineButBeforeTheJobIsRefusedWithoutEffect(int secondsAfterDeadline)
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline.AddSeconds(secondsAfterDeadline));

        using var late = await ChooseOptionAsync(alice, scene.Room, active.Version);

        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(late));
        var unknownRoom = await UnknownRoomAsync(alice, active.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(late, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());

        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        var expired = new RoomFacts("Expired", active.Version + 1, ActiveReservations: 0, Actions: 0);
        Assert.Equal(expired, await scene.RoomFactsAsync());

        using var afterTheJob = await ChooseOptionAsync(alice, scene.Room, active.Version);
        Assert.Equal(unknownRoom, await NeutralResponse.DescribeAsync(afterTheJob, Ct));
        Assert.Equal(expired, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// Uma ação nova com <c>expectedVersion</c> desatualizado, no prazo e antes do job, recebe a mesma <c>404</c> da ação
    /// atrasada e de uma sala inexistente, não o <c>409</c> de conflito: o prazo é checado antes da versão. Nada é gravado.
    /// </summary>
    [Fact]
    public async Task ANewActionWithAStaleVersionAtTheDeadlineGetsTheSame404()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        using (var applied = await PostActionAsync(alice, scene.Room, ChooseOption(active.Version)))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var stale = await PostActionAsync(alice, scene.Room, ChooseOption(active.Version));

        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(stale));
        Assert.Equal(await UnknownRoomAsync(alice, active.Version), await NeutralResponse.DescribeAsync(stale, Ct));
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
        Assert.Equal("Pending|0", await scene.GameDeadlineJobAsync());
    }

    /// <summary>Reutilizar um <c>actionId</c> já aplicado com outro corpo, antes do prazo, continua <c>400 VALIDATION_FAILED</c>.</summary>
    [Fact]
    public async Task AReusedActionIdBeforeTheDeadlineIsStillAValidationFailure()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        var chosen = ChooseOption(active.Version);
        using (var applied = await PostActionAsync(alice, scene.Room, chosen))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = await scene.RoomFactsAsync();

        using var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen));

        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused));
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// A regra de replay vale até o job rodar: a ação aplicada um tick antes do prazo, reenviada depois do prazo e antes do
    /// job, responde <c>204</c> sem gravar de novo, e o mesmo <c>actionId</c> com outro corpo continua <c>400</c>. Depois do
    /// job, os dois recebem <c>404</c>.
    /// </summary>
    [Fact]
    public async Task AnActionAppliedInTimeIsReplayedAfterTheDeadlineUntilTheJobRuns()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);
        var chosen = ChooseOption(active.Version);
        using (var applied = await PostActionAsync(alice, scene.Room, chosen))
        {
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }

        var afterTheAction = new RoomFacts("Active", active.Version + 1, ActiveReservations: 2, Actions: 1);
        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());
        scene.Clock.SetUtcNow(scene.Deadline);

        using (var replay = await PostActionAsync(alice, scene.Room, chosen))
        using (var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen)))
        {
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(reused));
        }

        Assert.Equal(afterTheAction, await scene.RoomFactsAsync());

        var job = await scene.Service.ClaimJobAsync(Ct);
        Assert.NotNull(job);
        Assert.True(await scene.Service.CompleteJobAsync(job, Ct));
        var expired = await scene.RoomFactsAsync();
        Assert.Equal("Expired", expired.State);

        using (var replay = await PostActionAsync(alice, scene.Room, chosen))
        using (var reused = await PostActionAsync(alice, scene.Room, SkipCardWithTheSameId(chosen)))
        {
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, reused.StatusCode);
        }

        Assert.Equal(expired, await scene.RoomFactsAsync());
    }

    /// <summary>Só o prazo virou <c>404</c>: uma ação inválida antes do prazo continua <c>400 VALIDATION_FAILED</c>, sem gravar.</summary>
    [Fact]
    public async Task AnInvalidActionBeforeTheDeadlineIsStillAValidationFailure()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        using var invalid = await alice.PostJsonAsync($"/api/v1/rooms/{scene.Room}/actions",
            new RoomActionRequest(Guid.NewGuid(), active.Version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "no-such-option"));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(invalid));
        Assert.Equal(active, await scene.RoomFactsAsync());
    }

    /// <summary>
    /// O prazo só vale para a ação bem formada. Um payload malformado no prazo é recusado antes de a sala ser lida: a
    /// resposta é igual à do mesmo payload numa sala inexistente, e nada é gravado. O handler recusa a versão inválida com
    /// <c>400 VALIDATION_FAILED</c>; JSON inválido e GUID malformado falham no model binding com <c>400 INVALID_REQUEST</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"actionId":"6f1c2a4e-0000-4000-8000-000000000001","expectedVersion":"0","type":"choose-option","cardId":"morning","optionId":"tea"}""", "VALIDATION_FAILED")]
    [InlineData("""{"actionId":"not-a-guid","expectedVersion":"1","type":"choose-option","cardId":"morning","optionId":"tea"}""", "INVALID_REQUEST")]
    [InlineData("""{"actionId":""", "INVALID_REQUEST")]
    public async Task AMalformedActionAtTheDeadlineIsRejectedBeforeTheRoomIsRead(string json, string code)
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline);

        using var malformed = await alice.PostRawJsonAsync($"/api/v1/rooms/{scene.Room}/actions", json);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(malformed));
        using var unknown = await alice.PostRawJsonAsync($"/api/v1/rooms/{Guid.NewGuid()}/actions", json);
        Assert.Equal(await NeutralResponse.DescribeAsync(unknown, Ct), await NeutralResponse.DescribeAsync(malformed, Ct));
        Assert.Equal(active, await scene.RoomFactsAsync());
    }

    [Fact]
    public async Task AnActionOneTickBeforeTheDeadlineIsApplied()
    {
        await using var scene = await ActiveRoomAsync();
        using var alice = await scene.SignInAliceAsync();
        var active = await scene.RoomFactsAsync();
        scene.Clock.SetUtcNow(scene.Deadline - OneTick);

        using var inTime = await ChooseOptionAsync(alice, scene.Room, active.Version);

        Assert.Equal(HttpStatusCode.NoContent, inTime.StatusCode);
        Assert.Equal(new RoomFacts("Active", active.Version + 1, ActiveReservations: 2, Actions: 1), await scene.RoomFactsAsync());
    }

    /// <summary>A mesma ação numa sala que não existe: a resposta que a sala vencida não pode distinguir.</summary>
    private static async Task<string> UnknownRoomAsync(Browser browser, long version)
    {
        using var unknown = await ChooseOptionAsync(browser, Guid.NewGuid(), version);
        return await NeutralResponse.DescribeAsync(unknown, Ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static RoomActionRequest ChooseOption(long version) =>
        new(Guid.NewGuid(), version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "tea");

    private static RoomActionRequest SkipCardWithTheSameId(RoomActionRequest action) =>
        action with { Type = "skip-card", OptionId = null };

    private static Task<HttpResponseMessage> PostActionAsync(Browser browser, Guid room, RoomActionRequest action) =>
        browser.PostJsonAsync($"/api/v1/rooms/{room}/actions", action);

    private static Task<HttpResponseMessage> ChooseOptionAsync(Browser browser, Guid room, long version) =>
        browser.PostJsonAsync($"/api/v1/rooms/{room}/actions",
            new RoomActionRequest(Guid.NewGuid(), version.ToString(CultureInfo.InvariantCulture), "choose-option", "morning", "tea"));

    private async Task<Scene> ActiveRoomAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var clock = new FakeTimeProvider(now);
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new ParticipationService(source, clock);
        var eventId = seed.Events[0];
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, Ct));
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
        var room = (await service.StatusAsync(eventId, seed.Alice, Ct))!.RoomId!.Value;
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Alice, true, Ct));
        Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, seed.Bob, true, Ct));
        var scene = new Scene(new DuoraWebFactory(connectionString) { Time = clock }, connectionString, source, clock, service, seed, room);
        await scene.IsolateGameDeadlineJobAsync();
        return scene;
    }

    private sealed record RoomFacts(string State, long Version, long ActiveReservations, long Actions);

    private sealed class Scene(DuoraWebFactory factory, string connectionString, NpgsqlDataSource source, FakeTimeProvider clock,
        ParticipationService service, ParticipationSeed seed, Guid room) : IAsyncDisposable
    {
        public FakeTimeProvider Clock => clock;

        public ParticipationService Service => service;

        public Guid Room => room;

        public DateTimeOffset Deadline { get; private set; }

        public Task<Browser> SignInAliceAsync() =>
            new IdentityScenario(connectionString, factory).SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));

        /// <summary>Lê o prazo gravado e adia os outros jobs pendentes, para o claim só poder escolher o <c>game-deadline</c> da sala.</summary>
        public async Task IsolateGameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using (var query = Command(connection, """
                select j.due_at,s.active_deadline_at from ops.jobs j join experiences.game_sessions s on s.id=j.aggregate_id
                where j.kind='game-deadline' and j.aggregate_id=$1
                """, room))
            await using (var reader = await query.ExecuteReaderAsync(Ct))
            {
                Assert.True(await reader.ReadAsync(Ct));
                Deadline = reader.GetFieldValue<DateTimeOffset>(0);
                Assert.Equal(Deadline, reader.GetFieldValue<DateTimeOffset>(1));
                Assert.False(await reader.ReadAsync(Ct));
            }

            await Execute(connection, """
                update ops.jobs set available_at=$2+interval '10 years'
                where status='Pending' and not (kind='game-deadline' and aggregate_id=$1)
                """, Ct, room, clock.GetUtcNow());
        }

        public async Task AnticipateGameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            Assert.Equal(1, await Execute(connection, "update ops.jobs set available_at=$2 where kind='game-deadline' and aggregate_id=$1", Ct, room, clock.GetUtcNow()));
        }

        /// <summary><c>status|tentativas</c>.</summary>
        public async Task<string> GameDeadlineJobAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select status||'|'||attempts from ops.jobs where kind='game-deadline' and aggregate_id=$1", room);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task<RoomFacts> RoomFactsAsync()
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select s.state,s.version,
                  (select count(*) from events.pairing_participants pp where pp.pairing_id=s.pairing_id and pp.released_at is null),
                  (select count(*) from experiences.game_actions a where a.game_session_id=s.id)
                from experiences.game_sessions s where s.id=$1
                """, room);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
