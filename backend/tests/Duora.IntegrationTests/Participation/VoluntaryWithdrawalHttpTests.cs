using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// API-E04 por HTTP, com cookie e CSRF (ADR-0053): a pessoa só retira a própria inscrição, o alvo nunca vem do cliente,
/// recurso alheio ou inexistente responde o mesmo <c>404</c> e repetir não repete efeito. 401, 403 de política e de
/// conta suspensa ficam com os testes que enumeram as rotas.
/// </summary>
public sealed class VoluntaryWithdrawalHttpTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithdrawingReturnsNoContentAndWithdrawsOnlyTheCaller()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var bobBefore = await scene.RowAsync(scene.Seed.Bob);

        using var response = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("Withdrawn|Confirmed", await scene.HistoryAsync(scene.Seed.Alice));
        Assert.NotEqual(bobBefore, await scene.RowAsync(scene.Seed.Bob));
        Assert.Equal("Confirmed|-", await scene.HistoryAsync(scene.Seed.Bob));
    }

    [Fact]
    public async Task RepeatingTheDeleteIsNoContentWithoutASecondEffect()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        using (var first = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId)))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }
        var after = await scene.SnapshotAsync();
        scene.Clock.Advance(TimeSpan.FromSeconds(1));

        using var second = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId));

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(after, await scene.SnapshotAsync());
    }

    [Fact]
    public async Task AnotherPersonDeletingOnTheSameEventGets404AndTheOwnerIsUntouched()
    {
        await using var scene = await Scene.CreateAsync(postgres, registerBob: false);
        var before = await scene.SnapshotAsync();

        using var response = await scene.Bob.DeleteAsync(Scene.Path(scene.EventId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(scene.Seed.Alice.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confirmed", body, StringComparison.Ordinal);
        Assert.Equal(before, await scene.SnapshotAsync());
    }

    [Fact]
    public async Task ABodyOrQueryCannotChooseTheTarget()
    {
        await using var scene = await Scene.CreateAsync(postgres, registerBob: false);
        var before = await scene.SnapshotAsync();
        var target = $$"""{"userId":"{{scene.Seed.Alice}}","state":"Withdrawn"}""";

        using var withQuery = await scene.Bob.DeleteAsync($"{Scene.Path(scene.EventId)}?userId={scene.Seed.Alice}");
        using var withBody = await scene.Bob.SendDeleteWithBodyAsync(Scene.Path(scene.EventId),
            new StringContent(target, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, withQuery.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withBody.StatusCode);
        Assert.Equal(before, await scene.SnapshotAsync());
    }

    [Fact]
    public async Task AnUnknownEventAnswersLikeAnEventWithoutTheCallersRegistration()
    {
        await using var scene = await Scene.CreateAsync(postgres, registerBob: false);

        using var unknown = await scene.Bob.DeleteAsync(Scene.Path(Guid.NewGuid()));
        using var notRegistered = await scene.Bob.DeleteAsync(Scene.Path(scene.EventId));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await NeutralAsync(notRegistered), await NeutralAsync(unknown));
    }

    /// <summary>
    /// Inscrição encerrada, evento fechado e evento já iniciado respondem exatamente como evento inexistente: mesmo
    /// status, headers e corpo, e nada muda no banco.
    /// </summary>
    [Theory]
    [InlineData("completed-registration")]
    [InlineData("cancelled-event")]
    [InlineData("running-event")]
    [InlineData("started")]
    public async Task ARegistrationThatCannotBeWithdrawnAnswersLikeAnUnknownEvent(string situation)
    {
        await using var scene = await Scene.CreateAsync(postgres, registerBob: false);
        switch (situation)
        {
            case "completed-registration":
                await scene.ExecuteAsync("update events.registrations set state='Completed' where event_id=$1 and user_id=$2",
                    scene.EventId, scene.Seed.Alice);
                break;
            case "cancelled-event":
                await scene.ExecuteAsync("update events.events set state='Cancelled' where id=$1", scene.EventId);
                break;
            case "running-event":
                await scene.ExecuteAsync("update events.events set state='Running' where id=$1", scene.EventId);
                break;
            default:
                scene.Clock.Advance(TimeSpan.FromMinutes(5));
                break;
        }
        var before = await scene.SnapshotAsync();

        using var refused = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId));
        using var unknown = await scene.Alice.DeleteAsync(Scene.Path(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(await NeutralAsync(unknown), await NeutralAsync(refused));
        Assert.Equal(before, await scene.SnapshotAsync());
    }

    [Fact]
    public async Task DeletingWithoutCsrfIs403AndChangesNothing()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var before = await scene.SnapshotAsync();

        using var response = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId), new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(before, await scene.SnapshotAsync());
    }

    [Fact]
    public async Task ALockTimeoutIs503WithRetryAfterAndNoEffect()
    {
        await using var scene = await Scene.CreateAsync(postgres);
        var before = await scene.SnapshotAsync();

        await using (await scene.HoldGuardAsync(scene.Seed.Alice))
        {
            using var response = await scene.Alice.DeleteAsync(Scene.Path(scene.EventId)).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
            Assert.DoesNotContain("SQL", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(before, await scene.SnapshotAsync());
    }

    /// <summary>O corpo sem o <c>traceId</c>, que muda por requisição.</summary>
    /// <summary>Headers que mudam por requisição: data e identificadores de rastreio.</summary>
    private static readonly HashSet<string> PerRequestHeaders = new(StringComparer.OrdinalIgnoreCase) { "Date", "traceparent", "Request-Id" };

    /// <summary>Status, headers (sem os que mudam por requisição) e corpo sem o <c>traceId</c>.</summary>
    private static async Task<string> NeutralAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !PerRequestHeaders.Contains(header.Key))
            .Select(header => $"{header.Key.ToLowerInvariant()}: {string.Join(",", header.Value)}")
            .Order(StringComparer.Ordinal);
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (text.Length > 0)
        {
            var body = JsonNode.Parse(text)!.AsObject();
            body.Remove("traceId");
            text = body.ToJsonString();
        }
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{text}";
    }

    /// <summary>Capacidade 1: Alice confirmada e, por padrão, Bob na posição 1 da fila.</summary>
    private sealed class Scene : IAsyncDisposable
    {
        private readonly DuoraWebFactory _factory;
        private readonly NpgsqlDataSource _source;

        private Scene(DuoraWebFactory factory, NpgsqlDataSource source, FakeTimeProvider clock, ParticipationSeed seed,
            Browser alice, Browser bob)
        {
            _factory = factory;
            _source = source;
            Clock = clock;
            Seed = seed;
            Alice = alice;
            Bob = bob;
        }

        public FakeTimeProvider Clock { get; }

        public ParticipationSeed Seed { get; }

        public Browser Alice { get; }

        public Browser Bob { get; }

        public Guid EventId => Seed.Events[0];

        public static async Task<Scene> CreateAsync(PostgresFixture postgres, bool registerBob = true)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            now = now.AddTicks(-now.Ticks % 10);
            var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
            var clock = new FakeTimeProvider(now);
            var factory = new DuoraWebFactory(connectionString) { Time = clock };
            var source = NpgsqlDataSource.Create(connectionString);
            await using (var connection = await source.OpenConnectionAsync(Ct))
            {
                await Execute(connection, "update events.events set capacity=1 where id=$1", Ct, seed.Events[0]);
            }
            var identity = new IdentityScenario(connectionString, factory);
            var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
            var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
            var scene = new Scene(factory, source, clock, seed, alice, bob);
            await scene.RegisterAsync(alice);
            if (registerBob)
            {
                await scene.RegisterAsync(bob);
            }
            return scene;
        }

        public static string Path(Guid eventId) => $"/api/v1/events/{eventId}/registration";

        /// <summary><c>estado|origem da retirada</c>.</summary>
        public Task<string> HistoryAsync(Guid user) => TextAsync(
            "select state||'|'||coalesce(withdrawn_from_state,'-') from events.registrations where event_id=$1 and user_id=$2",
            EventId, user);

        public async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await Execute(connection, sql, Ct, values);
        }

        public Task<string> RowAsync(Guid user) => TextAsync(
            "select r::text from events.registrations r where event_id=$1 and user_id=$2", EventId, user);

        /// <summary>Tudo que a retirada pode escrever: inscrições, evento, auditoria e outbox.</summary>
        public Task<string> SnapshotAsync() => TextAsync("""
            select jsonb_build_object(
              'event', (select to_jsonb(e) from events.events e where e.id=$1),
              'registrations', (select jsonb_agg(to_jsonb(r) order by r.user_id) from events.registrations r where r.event_id=$1),
              'audit', (select count(*) from ops.audit_events where action like 'events.%'),
              'outbox', (select count(*) from ops.outbox_events))::text
            """, EventId);

        /// <summary>Segura a guarda até o descarte: quem pedir a mesma linha espera até o <c>lock_timeout</c>.</summary>
        public async Task<IAsyncDisposable> HoldGuardAsync(Guid user)
        {
            var connection = await _source.OpenConnectionAsync(Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await Execute(connection, "insert into safety.user_guards(user_id) values($1) on conflict do nothing", Ct, user);
            await Execute(connection, "select 1 from safety.user_guards where user_id=$1 for update", Ct, user);
            return new HeldGuard(connection, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            Bob.Dispose();
            await _source.DisposeAsync();
            await _factory.DisposeAsync();
        }

        private async Task RegisterAsync(Browser browser)
        {
            using var response = await browser.PutAsync(Path(EventId));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        private async Task<string> TextAsync(string sql, params object[] values)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, sql, values);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
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
