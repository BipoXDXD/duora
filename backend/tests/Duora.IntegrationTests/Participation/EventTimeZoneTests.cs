using System.Net;
using System.Text.Json;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class EventTimeZoneTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PublicationPreservesIanaZoneAndUtcInstantAndRejectsUnknownZone()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateMigratedDatabaseAsync(ct);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, clock.GetUtcNow(), ct, declarePreferences: true);
        await using var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var identity = new IdentityScenario(connectionString, factory);
        var admin = await identity.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
        using var administrator = await identity.SignedInBrowserAsync(admin);
        await MfaScenario.EnableAsync(administrator, clock);
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, "update identity.users set status='Active' where id=$1", ct, admin.UserId);
        await using var query = Command(connection, "select experience_version_id from events.events where id=$1", seed.Events[0]);
        var contentId = (Guid)(await query.ExecuteScalarAsync(ct))!;
        var start = clock.GetUtcNow().AddDays(2);
        foreach (var zone in new[] { "America/New_York", "America/Sao_Paulo", "UTC", "Mars/Olympus", "Eastern Standard Time", "" })
        {
            using var response = await administrator.PostJsonAsync("/api/v1/admin/events", new { title = "Zone test " + zone, startsAt = start, capacity = 10, experienceVersionId = contentId, timeZone = zone });
            Assert.Equal(zone is "Mars/Olympus" or "Eastern Standard Time" or "" ? HttpStatusCode.BadRequest : HttpStatusCode.Created, response.StatusCode);
        }
        using var withoutZone = await administrator.PostJsonAsync("/api/v1/admin/events", new { title = "Without zone", startsAt = start, capacity = 10, experienceVersionId = contentId });
        Assert.Equal(HttpStatusCode.Created, withoutZone.StatusCode);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        using var list = await alice.GetAsync("/api/v1/events");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));
        foreach (var zone in new[] { "America/New_York", "America/Sao_Paulo", "UTC" })
        {
            var item = Assert.Single(json.RootElement.EnumerateArray(), e => e.GetProperty("title").GetString() == "Zone test " + zone);
            Assert.Equal(zone, item.GetProperty("timeZone").GetString());
            Assert.Equal(start, item.GetProperty("startsAt").GetDateTimeOffset());
        }
        var omitted = Assert.Single(json.RootElement.EnumerateArray(), e => e.GetProperty("title").GetString() == "Without zone");
        Assert.Equal(JsonValueKind.Null, omitted.GetProperty("timeZone").ValueKind);
        var legacy = Assert.Single(json.RootElement.EnumerateArray(), e => e.GetProperty("eventId").GetGuid() == seed.Events[0]);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("timeZone").ValueKind);
    }
}
