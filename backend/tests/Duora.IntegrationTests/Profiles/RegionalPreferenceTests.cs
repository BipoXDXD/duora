using System.Net;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionalPreferenceTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task UndeclaredAccountReturnsPendingWithoutCreatingARow()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var read = await browser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("\"0\"", read.Headers.ETag!.ToString());
        Assert.Contains("no-store", read.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("pending", (string?)body["mode"]);
        Assert.Empty(body["countryCodes"]!.AsArray());
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("select count(*) from profiles.regional_preferences where user_id=$1", connection);
        count.Parameters.AddWithValue(account.UserId);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Ct));
        using var languages = await browser.GetAsync("/api/v1/me/preferences");
        Assert.Equal("\"0\"", languages.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentUpdatesHaveOneWinnerAndNoPartialCountries(bool previouslySaved)
    {
        using var browser = await BrowserAsync();
        if (previouslySaved)
        {
            using var first = await PutAsync(browser, """{"mode":"countries","countryCodes":["BR"]}""", "0");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        var version = previouslySaved ? "1" : "0";
        var responses = await Task.WhenAll(
            PutAsync(browser, """{"mode":"countries","countryCodes":["BR"]}""", version),
            PutAsync(browser, """{"mode":"unrestricted","countryCodes":[]}""", version));
        try
        {
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(r => r.StatusCode).Order());
            using var read = await browser.GetAsync("/api/v1/me/regional-preferences");
            Assert.Equal(previouslySaved ? "\"2\"" : "\"1\"", read.Headers.ETag!.ToString());
            var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(Ct))!;
            if ((string?)body["mode"] == "countries")
            {
                Assert.Equal("BR", (string?)Assert.Single(body["countryCodes"]!.AsArray()));
            }
            else
            {
                Assert.Equal("unrestricted", (string?)body["mode"]);
                Assert.Empty(body["countryCodes"]!.AsArray());
            }
        }
        finally
        {
            foreach (var response in responses)
            { response.Dispose(); }
        }
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"mode":"pending","countryCodes":[]}""")]
    [InlineData("""{"mode":"countries","countryCodes":[]}""")]
    [InlineData("""{"mode":"countries","countryCodes":["US"]}""")]
    [InlineData("""{"mode":"countries","countryCodes":["BR","BR"]}""")]
    [InlineData("""{"mode":"countries","countryCodes":[null]}""")]
    [InlineData("""{"mode":"unrestricted","countryCodes":["BR"]}""")]
    [InlineData("""{"mode":"unrestricted","countryCodes":null}""")]
    [InlineData("""{"mode":"unrestricted"}""")]
    [InlineData("""{"mode":"unrestricted","countryCodes":[],"userId":"other"}""")]
    public async Task InvalidInputDoesNotCreateConsent(string json)
    {
        using var browser = await BrowserAsync();
        using var response = await PutAsync(browser, json, "0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("US", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var read = await browser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal("\"0\"", read.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("W/\"0\"", HttpStatusCode.PreconditionFailed)]
    [InlineData("\"01\"", HttpStatusCode.PreconditionFailed)]
    [InlineData("\"0\",\"1\"", HttpStatusCode.PreconditionFailed)]
    [InlineData("*", (HttpStatusCode)428)]
    public async Task NonVersionPreconditionsDoNotWrite(string tag, HttpStatusCode status)
    {
        using var browser = await BrowserAsync();
        using var response = await browser.PutAsync("/api/v1/me/regional-preferences", """{"mode":"unrestricted","countryCodes":[]}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = tag }));
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task PreconditionsCsrfAndAuthenticationAreRequired()
    {
        const string Body = """{"mode":"unrestricted","countryCodes":[]}""";
        using var browser = await BrowserAsync();
        using var missing = await browser.PutAsync("/api/v1/me/regional-preferences", Body);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var csrf = await browser.PutAsync("/api/v1/me/regional-preferences", Body,
            new MutationHeaders(SendCsrfToken: false, Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        using var anonymous = new Browser(_factory);
        using var read = await anonymous.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [Fact]
    public async Task RegionalAndLanguageVersionsAreIndependentAndGuardAdvancesOnlyOnSuccess()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var profile = await browser.PatchRawJsonAsync("/api/v1/me/profile", """{"countryCode":"BR","subdivisionCode":"BR-SP"}""", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var saved = await PutAsync(browser, """{"mode":"countries","countryCodes":["BR"]}""", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag!.ToString());
        Assert.Contains("no-store", saved.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var stale = await PutAsync(browser, """{"mode":"unrestricted","countryCodes":[]}""", "0");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var languages = await browser.GetAsync("/api/v1/me/preferences");
        using var locale = await browser.GetAsync("/api/v1/me/locale");
        using var location = await browser.GetAsync("/api/v1/me/profile");
        Assert.Equal("\"0\"", languages.Headers.ETag!.ToString());
        Assert.Equal("\"0\"", locale.Headers.ETag!.ToString());
        Assert.Equal("\"1\"", location.Headers.ETag!.ToString());
        var locationBody = JsonNode.Parse(await location.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("BR-SP", (string?)locationBody["subdivisionCode"]);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var guard = new NpgsqlCommand("select security_version from safety.user_guards where user_id=$1", connection);
        guard.Parameters.AddWithValue(account.UserId);
        Assert.Equal(1L, await guard.ExecuteScalarAsync(Ct));
        using var languageEdit = await browser.PutAsync("/api/v1/me/preferences",
            """{"communicationLanguages":["pt"],"allowDifferentDisplayLocales":false}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.OK, languageEdit.StatusCode);
        using var regionalRead = await browser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal("\"1\"", regionalRead.Headers.ETag!.ToString());
        Assert.Equal("countries", (string?)JsonNode.Parse(await regionalRead.Content.ReadAsStringAsync(Ct))!["mode"]);
    }

    [Fact]
    public async Task AnotherAccountCannotReadOrChangeTheOwnersPreference()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        using var ownerBrowser = await scenario.SignedInBrowserAsync(owner);
        using var saved = await PutAsync(ownerBrowser, """{"mode":"countries","countryCodes":["BR"]}""", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var other = await BrowserAsync();
        using var read = await other.GetAsync($"/api/v1/me/regional-preferences?userId={owner.UserId}");
        Assert.Equal("pending", (string?)JsonNode.Parse(await read.Content.ReadAsStringAsync(Ct))!["mode"]);
        using var byId = await other.GetAsync($"/api/v1/me/regional-preferences/{owner.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        using var edited = await other.PutAsync($"/api/v1/me/regional-preferences?userId={owner.UserId}",
            """{"mode":"unrestricted","countryCodes":[]}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        using var ownerRead = await ownerBrowser.GetAsync("/api/v1/me/regional-preferences");
        Assert.Equal("countries", (string?)JsonNode.Parse(await ownerRead.Content.ReadAsStringAsync(Ct))!["mode"]);
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    [InlineData("DeletionPending")]
    public async Task RestrictedAccountCannotWrite(string status)
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("update identity.users set status=$1 where id=$2", connection);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(account.UserId);
        await command.ExecuteNonQueryAsync(Ct);
        using var response = await PutAsync(browser, """{"mode":"unrestricted","countryCodes":[]}""", "0");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<Browser> BrowserAsync()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        return await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
    }

    private static Task<HttpResponseMessage> PutAsync(Browser browser, string body, string version) =>
        browser.PutAsync("/api/v1/me/regional-preferences", body,
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" }));
}
