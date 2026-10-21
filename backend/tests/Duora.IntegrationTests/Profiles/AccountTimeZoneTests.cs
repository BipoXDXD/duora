using System.Net;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.IntegrationTests.Profiles;

public sealed class AccountTimeZoneTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ExplicitSaveAndRemovalPreserveThePrivateZoneWithoutDeclaringLanguages()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var identity = new IdentityScenario(postgres, factory);
        using var browser = await identity.SignedInBrowserAsync(await identity.CreateConfirmedAccountAsync());
        using var initial = await browser.GetAsync("/api/v1/me/time-zone");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal("\"0\"", initial.Headers.ETag!.ToString());
        Assert.Null((await BodyAsync(initial))["timeZone"]);
        var version = 0;
        foreach (var zone in new string?[] { "America/New_York", "America/Sao_Paulo", "UTC", null })
        {
            using var saved = await PutAsync(browser, zone, version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(zone, (string?)(await BodyAsync(saved))["timeZone"]);
            version++;
            Assert.Equal($"\"{version}\"", saved.Headers.ETag!.ToString());
            using var me = await browser.GetAsync("/api/v1/me");
            Assert.Equal(zone, (string?)(await BodyAsync(me))["preferredTimeZone"]);
        }
        using var preferences = await browser.GetAsync("/api/v1/me/preferences");
        var declaration = await BodyAsync(preferences);
        Assert.True((bool)declaration["declarationRequired"]!);
        Assert.Empty(declaration["communicationLanguages"]!.AsArray());
        using var options = await browser.GetAsync("/api/v1/me/time-zone/options");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var ids = (await BodyAsync(options))["timeZones"]!.AsArray().Select(x => (string)x!).ToArray();
        Assert.Contains("America/New_York", ids);
        Assert.Contains("America/Sao_Paulo", ids);
        Assert.Contains("UTC", ids);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("Eastern Standard Time")]
    [InlineData("GMT-3")]
    [InlineData("")]
    public async Task InvalidZoneDoesNotReplaceASavedChoice(string zone)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var identity = new IdentityScenario(postgres, factory);
        using var browser = await identity.SignedInBrowserAsync(await identity.CreateConfirmedAccountAsync());
        using var saved = await PutAsync(browser, "America/New_York", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var invalid = await PutAsync(browser, zone, "1");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var omitted = await browser.PutAsync("/api/v1/me/time-zone", "{}", Headers("1"));
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        using var current = await browser.GetAsync("/api/v1/me/time-zone");
        Assert.Equal("America/New_York", (string?)(await BodyAsync(current))["timeZone"]);
        Assert.Equal("\"1\"", current.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task ConditionalConcurrentWritesHaveOneWinnerAndCannotEditAnotherAccount()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var identity = new IdentityScenario(postgres, factory);
        using var alice = await identity.SignedInBrowserAsync(await identity.CreateConfirmedAccountAsync());
        using var bob = await identity.SignedInBrowserAsync(await identity.CreateConfirmedAccountAsync());
        using var missing = await alice.PutAsync("/api/v1/me/time-zone", """{"timeZone":"UTC"}""");
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        var responses = await Task.WhenAll(PutAsync(alice, "America/New_York", "0"), PutAsync(alice, "America/Sao_Paulo", "0"));
        try
        {
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(x => x.StatusCode).Order());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
        using var other = await bob.GetAsync("/api/v1/me/time-zone");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Null((await BodyAsync(other))["timeZone"]);
        Assert.Equal("\"0\"", other.Headers.ETag!.ToString());
        using var anonymous = new Browser(factory);
        using var unauthenticated = await anonymous.GetAsync("/api/v1/me/time-zone");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task AnIdentityUpdateWithAStaleEntityCannotOverwriteTheIndependentPreference()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var identity = new IdentityScenario(postgres, factory);
        var account = await identity.CreateConfirmedAccountAsync();
        using var browser = await identity.SignedInBrowserAsync(account);
        await using var db = PostgresFixture.CreateDbContext(postgres.ConnectionString);
        var user = await db.Users.SingleAsync(x => x.Id == account.UserId, TestContext.Current.CancellationToken);
        using var saved = await PutAsync(browser, "America/New_York", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // Identity's UpdateAsync marks the whole entity modified, even fields it does not own.
        db.Update(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var current = await browser.GetAsync("/api/v1/me/time-zone");
        Assert.Equal("America/New_York", (string?)(await BodyAsync(current))["timeZone"]);
        Assert.Equal("\"1\"", current.Headers.ETag!.ToString());
    }

    private static MutationHeaders Headers(string version) => new(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" });
    private static Task<HttpResponseMessage> PutAsync(Browser browser, string? zone, string version) =>
        browser.PutAsync("/api/v1/me/time-zone", new JsonObject { ["timeZone"] = zone }.ToJsonString(), Headers(version));
    private static async Task<JsonNode> BodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
}
