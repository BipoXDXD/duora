using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>Real authenticated HTTP writes and PostgreSQL snapshots feed the production V3 coordinator.</summary>
public sealed class RegionalMatchingApiTests(PostgresFixture postgres)
{
    // Country declarations and expected outcomes are independent, literal fixtures. '*' is an explicit
    // unrestricted API choice; null leaves the preference unsaved. No regional row is seeded directly.
    [Theory]
    [InlineData("BR", "US", "BR,US", "BR,US", "compatible", true)]
    [InlineData("BR", "US", "US", "BR", "compatible", true)]
    [InlineData("US", "BR", "BR", "BR,US", "compatible", true)]
    [InlineData("US", "US", "US", "US", "compatible", true)]
    [InlineData("BR", "US", "US", "US", "compatible", false)]
    [InlineData("BR", "US", "BR", "BR,US", "compatible", false)]
    [InlineData("BR", "BR", "US", "US", "compatible", false)]
    [InlineData("BR", "US", null, "BR,US", "compatible", false)]
    [InlineData("BR", "US", "BR,US", null, "compatible", false)]
    [InlineData(null, "US", "BR,US", "*", "compatible", true)]
    [InlineData(null, "US", "*", "BR,US", "compatible", false)]
    [InlineData(null, null, "*", "*", "compatible", true)]
    [InlineData("BR", "US", "BR,US", "BR,US", "language", false)]
    [InlineData("BR", "US", "BR,US", "BR,US", "locale", false)]
    public async Task ApiChoicesDeterminePersistedV3PairsWithoutInferringCountryOrLanguageConsent(
        string? firstCountry, string? secondCountry, string? firstChoice, string? secondChoice,
        string communication, bool paired)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var suffix = Guid.NewGuid().ToString("N");
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, clock.GetUtcNow(), ct, suffix, true);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Time = clock };
        var identity = new IdentityScenario(postgres, factory);
        using var alice = await identity.SignedInBrowserAsync(new(seed.Alice, $"alice{suffix}@example.com", IdentityScenario.ValidPassword));
        using var bob = await identity.SignedInBrowserAsync(new(seed.Bob, $"bob{suffix}@example.com", IdentityScenario.ValidPassword));
        await ConfigureAsync(alice, firstCountry, firstChoice, "pt-BR", "en", communication != "locale");
        await ConfigureAsync(bob, secondCountry, secondChoice, "en-US", communication == "language" ? "pt" : "en", true);

        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(ct);
        // Synthetic persisted round only: no cutover configuration, publication change or production data.
        await Execute(connection, "update events.rounds set algorithm_version=3 where id=$1", ct, seed.Rounds[0]);
        foreach (var browser in new[] { alice, bob })
        {
            using var registration = await browser.PutAsync($"/api/v1/events/{seed.Events[0]}/registration");
            Assert.Equal(HttpStatusCode.NoContent, registration.StatusCode);
            using var checkIn = await browser.PostJsonAsync($"/api/v1/events/{seed.Events[0]}/check-in", null);
            Assert.Equal(HttpStatusCode.NoContent, checkIn.StatusCode);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        var coordinator = factory.Services.GetRequiredService<ParticipationService>();
        await coordinator.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        // A fresh compatible consumer without cutover must honor the stored V3 version and be idempotent.
        await new ParticipationService(source, clock).BuildRoundPairingsAsync(seed.Rounds[0], ct);

        Guid? firstRoom = null;
        foreach (var browser in new[] { alice, bob })
        {
            using var status = await browser.GetAsync($"/api/v1/events/{seed.Events[0]}/participation");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var body = JsonNode.Parse(await status.Content.ReadAsStringAsync(ct))!;
            var room = body["roomId"]?.GetValue<Guid>();
            Assert.Equal(paired, room.HasValue);
            if (paired)
            {
                Assert.Equal(firstRoom ?? room, room);
                firstRoom = room;
                using var view = await browser.GetAsync($"/api/v1/rooms/{room}");
                Assert.Equal(HttpStatusCode.OK, view.StatusCode);
                Assert.Equal("en", (string?)JsonNode.Parse(await view.Content.ReadAsStringAsync(ct))!["communicationLanguage"]);
            }
        }

        await using (var query = Command(connection, """
            select count(*),min(algorithm_version),min(communication_language)
            from events.pairings where round_id=$1
            """, seed.Rounds[0]))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(paired ? 1L : 0L, reader.GetInt64(0));
            if (paired)
            {
                Assert.Equal(3, reader.GetInt32(1));
                Assert.Equal("en", reader.GetString(2));
            }
        }
        await using (var reservations = Command(connection,
            "select count(*) from events.pairing_participants where round_id=$1 and released_at is null", seed.Rounds[0]))
        {
            Assert.Equal(paired ? 2L : 0L, await reservations.ExecuteScalarAsync(ct));
        }
        foreach (var (browser, user, choice) in new[] { (alice, seed.Alice, firstChoice), (bob, seed.Bob, secondChoice) })
        {
            await AssertChoiceAsync(browser, choice);
            if (choice is null)
            {
                await using var pending = Command(connection,
                    "select count(*) from profiles.regional_preferences where user_id=$1", user);
                Assert.Equal(0L, await pending.ExecuteScalarAsync(ct));
            }
        }
    }

    private static async Task ConfigureAsync(Browser browser, string? country, string? choice,
        string locale, string language, bool crossLocale)
    {
        using var profile = await browser.PatchRawJsonAsync("/api/v1/me/profile",
            JsonSerializer.Serialize(new { countryCode = country }), "\"0\"");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var localeSave = await browser.PutAsync("/api/v1/me/locale", JsonSerializer.Serialize(new { locale }), Version("0"));
        Assert.Equal(HttpStatusCode.OK, localeSave.StatusCode);
        using var languages = await browser.PutAsync("/api/v1/me/preferences",
            JsonSerializer.Serialize(new { communicationLanguages = new[] { language }, allowDifferentDisplayLocales = crossLocale }), Version("1"));
        Assert.Equal(HttpStatusCode.OK, languages.StatusCode);
        if (choice is not null)
        {
            var mode = choice == "*" ? "unrestricted" : "countries";
            var countryCodes = choice == "*" ? Array.Empty<string>() : choice.Split(',');
            using var saved = await browser.PutAsync("/api/v1/me/regional-preferences",
                JsonSerializer.Serialize(new { mode, countryCodes }), Version("0"));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        await AssertChoiceAsync(browser, choice);
        using var declared = await browser.GetAsync("/api/v1/me/profile");
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        Assert.Equal(country, (string?)JsonNode.Parse(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["countryCode"]);
    }

    private static async Task AssertChoiceAsync(Browser browser, string? choice)
    {
        using var read = await browser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(choice is null ? "\"0\"" : "\"1\"", read.Headers.ETag!.ToString());
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(choice is null ? "pending" : choice == "*" ? "unrestricted" : "countries", (string?)body["mode"]);
        var expected = choice is null or "*" ? Array.Empty<string>() : choice.Split(',');
        Assert.Equal(expected, body["countryCodes"]!.AsArray().Select(code => (string?)code));
    }

    private static MutationHeaders Version(string version) =>
        new(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" });
}
