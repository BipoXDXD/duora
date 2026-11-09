using System.Net;
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

        public async ValueTask DisposeAsync()
        {
            _browser.Dispose();
            await _source.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }
}
