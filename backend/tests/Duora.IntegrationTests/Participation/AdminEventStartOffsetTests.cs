using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Duora.Contracts.Participation;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Sem offset, o instante de um evento dependeria do fuso do servidor: o contrato (date-time, RFC 3339) exige offset.</summary>
public sealed class AdminEventStartOffsetTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("2026-12-01T20:00")]
    [InlineData("2026-12-01T20:00:00")]
    [InlineData("2026-12-01T20:00:00.1234567")]
    [InlineData("2026-12-01")]
    public async Task AStartWithoutAnOffsetIsRejectedWithoutSideEffects(string startsAt)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);
        var before = await admin.ScheduleRowsAsync();

        using var response = await admin.PublishAsync(startsAt);

        var body = await response.Content.ReadAsStringAsync(Ct);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        foreach (var internalDetail in new[] { "Exception", "Duora.Contracts", "   at " })
        {
            Assert.DoesNotContain(internalDetail, body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await admin.ScheduleRowsAsync());
    }

    [Theory]
    [InlineData("2026-12-01T20:00:00Z")]
    [InlineData("2026-12-01T20:00:00+00:00")]
    public async Task AStartWithAnOffsetIsPublishedAtThatInstant(string startsAt)
    {
        await using var admin = await AdminScenario.CreateAsync(postgres);

        using var response = await admin.PublishAsync(startsAt);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var eventId = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["eventId"]!.GetValue<Guid>();
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 20, 0, 0, TimeSpan.Zero), await admin.StartOfAsync(eventId));
    }

    [Theory]
    [InlineData("2026-12-01T20:00:00Z", 0)]
    [InlineData("2026-12-01T20:00:00\\u005A", 0)]
    [InlineData("2026-12-01T20:00:00+00:00", 0)]
    [InlineData("2026-12-01T20:00:00-03:00", -180)]
    [InlineData("2026-12-01T20:00:00+05:30", 330)]
    public void TheBinderKeepsAnExplicitOffset(string startsAt, int offsetMinutes)
    {
        var request = Bind($"\"{startsAt}\"");

        Assert.Equal(new DateTimeOffset(2026, 12, 1, 20, 0, 0, TimeSpan.FromMinutes(offsetMinutes)), request.StartsAt);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), request.StartsAt.Offset);
    }

    [Theory]
    [InlineData("\"2026-12-01T20:00:00\"")]
    [InlineData("\"2026-12-01T20:00:00.5\"")]
    [InlineData("\"2026-12-01\"")]
    [InlineData("\"amanhã\"")]
    [InlineData("\"\"")]
    [InlineData("\"2026-12-01T20:00:00-3\"")]
    [InlineData("1796155200")]
    [InlineData("true")]
    [InlineData("null")]
    public void TheBinderRefusesAStartThatIsNotAnInstant(string startsAt) =>
        Assert.Throws<JsonException>(() => Bind(startsAt));

    private static PublishEventRequest Bind(string startsAtJson) =>
        JsonSerializer.Deserialize<PublishEventRequest>(
            $$"""{"title":"Evento","startsAt":{{startsAtJson}},"capacity":10,"experienceVersionId":"{{Guid.Empty}}"}""",
            JsonSerializerOptions.Web)!;

    private sealed class AdminScenario : IAsyncDisposable
    {
        private readonly DuoraWebFactory _factory;
        private readonly Browser _browser;
        private readonly NpgsqlDataSource _source;
        private readonly Guid _contentId;

        private AdminScenario(DuoraWebFactory factory, Browser browser, NpgsqlDataSource source, Guid contentId)
        {
            _factory = factory;
            _browser = browser;
            _source = source;
            _contentId = contentId;
        }

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
            return new(factory, browser, source, seed.ContentId);
        }

        public Task<HttpResponseMessage> PublishAsync(string startsAt) =>
            _browser.PostJsonAsync("/api/v1/admin/events", new { title = "Evento", startsAt, capacity = 10, experienceVersionId = _contentId });

        public async Task<(long Events, long Jobs)> ScheduleRowsAsync()
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select (select count(*) from events.events),(select count(*) from ops.jobs)");
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public async Task<DateTimeOffset> StartOfAsync(Guid eventId)
        {
            await using var connection = await _source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select starts_at from events.events where id=$1", eventId);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return reader.GetFieldValue<DateTimeOffset>(0);
        }

        public async ValueTask DisposeAsync()
        {
            _browser.Dispose();
            await _source.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }
}
