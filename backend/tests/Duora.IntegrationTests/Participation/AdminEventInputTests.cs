using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Entrada de POST /admin/events: valor impossível de gravar ou agendar é 400, nunca 500, e não deixa rastro.</summary>
public sealed class AdminEventInputTests(PostgresFixture postgres)
{
    private const string ValidTitle = "Evento válido";

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Último início aceito: as rodadas e o fechamento somam 36 min, com folga para o arredondamento do PostgreSQL.</summary>
    private static readonly DateTimeOffset LatestStart = DateTimeOffset.MaxValue.AddHours(-1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Evento\u0000nulo")]
    [InlineData("Evento\u0001controle")]
    [InlineData("Evento\nquebrado")]
    [InlineData("Evento\u0085controle")]
    public Task ATitleWithAControlCharacterIsRejectedWithoutSideEffects(string title) =>
        AssertRejectedAsync(new { title, startsAt = Now.AddDays(2), capacity = 10 });

    [Fact]
    public Task ATimeZoneWithANulCharacterIsRejectedWithoutSideEffects() =>
        AssertRejectedAsync(new { title = ValidTitle, startsAt = Now.AddDays(2), capacity = 10, timeZone = "UTC\u0000" });

    [Fact]
    public Task AStartPastTheLastSchedulableInstantIsRejectedWithoutSideEffects() =>
        AssertRejectedAsync(new { title = ValidTitle, startsAt = LatestStart.AddTicks(10), capacity = 10 });

    [Fact]
    public Task AStartWhoseScheduleOverflowsTheCalendarIsRejectedWithoutSideEffects() =>
        AssertRejectedAsync(new { title = ValidTitle, startsAt = DateTimeOffset.MaxValue, capacity = 10 });

    [Theory]
    [InlineData("2026-12-01T20:00:00Z", "2026-12-01T20:00:00Z")]
    [InlineData("2026-12-01T20:00:00+00:00", "2026-12-01T20:00:00Z")]
    [InlineData("2026-12-01T20:00:00-03:00", "2026-12-01T23:00:00Z")]
    [InlineData("2026-12-01T20:00:00+05:30", "2026-12-01T14:30:00Z")]
    public async Task AStartInAnyOffsetIsStoredAsTheSameUtcInstant(string startsAt, string expectedUtc)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);
        var expected = DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture);

        using var response = await admin.PublishAsync(new { title = ValidTitle, startsAt, capacity = 10 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var eventId = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["eventId"]!.GetValue<Guid>();
        Assert.Equal((expected, expected.AddMinutes(24), expected.AddMinutes(36)), await admin.ScheduleOfAsync(eventId));
        Assert.Equal((5L, 14L), await admin.ScheduleRowsAsync());
    }

    /// <summary>O limite vale para o instante em UTC, em qualquer offset. O último caso é de relógio local que não cabe depois de somar a agenda.</summary>
    [Theory]
    [InlineData("9999-12-31T19:59:59.9999999-03:00")]
    [InlineData("9999-12-31T23:59:59.9999999+01:00")]
    [InlineData("9999-12-31T23:59:59+14:00")]
    public async Task AStartInsideTheUtcLimitsIsAcceptedInAnyOffset(string startsAt)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);

        using var response = await admin.PublishAsync(new { title = ValidTitle, startsAt, capacity = 10 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((5L, 14L), await admin.ScheduleRowsAsync());
    }

    [Theory]
    [InlineData("9999-12-31T20:00:00-03:00")]
    [InlineData("9999-12-31T23:30:00+00:30")]
    public Task AStartOutsideTheUtcLimitsIsRejectedInAnyOffset(string startsAt) =>
        AssertRejectedAsync(new { title = ValidTitle, startsAt, capacity = 10 });

    /// <summary>Calculado do relógio do cenário, que o cadastro do MFA avança. Um passo de 1 µs é a menor diferença gravável.</summary>
    [Theory]
    [InlineData(-180, 0, false)]
    [InlineData(-180, 10, true)]
    [InlineData(330, 0, false)]
    [InlineData(330, 10, true)]
    public async Task TheEarliestStartIsComparedInUtcInAnyOffset(int offsetMinutes, int ticksAfterTheLimit, bool accepted)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);
        var startsAt = admin.Clock.GetUtcNow().AddMinutes(10).AddTicks(ticksAfterTheLimit).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

        using var response = await admin.PublishAsync(new { title = ValidTitle, startsAt, capacity = 10 });

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(accepted ? (5L, 14L) : (4L, 8L), await admin.ScheduleRowsAsync());
    }

    [Fact]
    public async Task AnInstantBeyondTheCalendarInUtcIsRejectedWithoutSideEffects()
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);
        var before = await admin.ScheduleRowsAsync();

        using var response = await admin.PublishAsync(new { title = ValidTitle, startsAt = "9999-12-31T23:59:59-03:00", capacity = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await admin.ScheduleRowsAsync());
    }

    [Fact]
    public async Task AVisibleTitleAndTheLastSchedulableStartAreStillAccepted()
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);

        using var response = await admin.PublishAsync(new { title = "Encontro de ação ❤️", startsAt = LatestStart, capacity = 10 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((5L, 14L), await admin.ScheduleRowsAsync());
    }

    private async Task AssertRejectedAsync(object input)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);
        var before = await admin.ScheduleRowsAsync();

        using var response = await admin.PublishAsync(input);

        var body = await response.Content.ReadAsStringAsync(Ct);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        foreach (var internalDetail in new[] { "Npgsql", "Postgres", "Exception", "22021", "   at " })
        {
            Assert.DoesNotContain(internalDetail, body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await admin.ScheduleRowsAsync());
    }

    private sealed class AdminScenario : IAsyncDisposable
    {
        private readonly DuoraWebFactory _factory;
        private readonly Browser _browser;
        private readonly NpgsqlDataSource _source;
        private readonly Guid _contentId;

        private AdminScenario(DuoraWebFactory factory, Browser browser, NpgsqlDataSource source, Guid contentId, FakeTimeProvider clock)
        {
            Clock = clock;
            _factory = factory;
            _browser = browser;
            _source = source;
            _contentId = contentId;
        }

        public FakeTimeProvider Clock { get; }

        public static async Task<AdminScenario> CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
            var clock = new FakeTimeProvider(Now);
            var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, clock.GetUtcNow(), Ct, declarePreferences: true);
            var factory = new DuoraWebFactory(connectionString) { Time = clock };
            var identity = new IdentityScenario(connectionString, factory);
            var account = await identity.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
            var browser = await identity.SignedInBrowserAsync(account);
            await MfaScenario.EnableAsync(browser, clock);
            var source = NpgsqlDataSource.Create(connectionString);
            await using var connection = await source.OpenConnectionAsync(Ct);
            await Execute(connection, "update identity.users set status='Active' where id=$1", Ct, account.UserId);
            return new(factory, browser, source, seed.ContentId, clock);
        }

        public Task<HttpResponseMessage> PublishAsync(object input)
        {
            var body = System.Text.Json.JsonSerializer.SerializeToNode(input)!.AsObject();
            body["experienceVersionId"] = _contentId;
            return _browser.PostJsonAsync("/api/v1/admin/events", body);
        }

        /// <summary>Eventos e jobs de rodada: a seed cria 4 eventos e 8 jobs; publicar soma 1 evento e 3 rodadas de 2 jobs.</summary>
        public async Task<(long Events, long Jobs)> ScheduleRowsAsync()
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select (select count(*) from events.events),(select count(*) from ops.jobs)");
            await using var reader = await query.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        /// <summary>Início do evento, início da 3ª rodada e prazo dela.</summary>
        public async Task<(DateTimeOffset Start, DateTimeOffset LastRound, DateTimeOffset LastDeadline)> ScheduleOfAsync(Guid eventId)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, """
                select e.starts_at,r.starts_at,r.deadline_at from events.events e join events.rounds r on r.event_id=e.id
                where e.id=$1 and r.number=3
                """, eventId);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        }

        public async ValueTask DisposeAsync()
        {
            _browser.Dispose();
            await _source.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }
}
