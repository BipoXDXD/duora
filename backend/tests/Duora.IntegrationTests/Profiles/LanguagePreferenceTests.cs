using System.Net;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class LanguagePreferenceTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task UndeclaredAccountHasNoImplicitLanguages()
    {
        using var browser = await BrowserAsync();
        using var response = await browser.GetAsync("/api/v1/me/preferences");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.True((bool)body["declarationRequired"]!);
        Assert.Empty(body["communicationLanguages"]!.AsArray());
        Assert.Equal("\"0\"", response.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDeclarationHasExactlyOneWinner(bool previouslySaved)
    {
        using var browser = await BrowserAsync();
        const string Body = """{"communicationLanguages":["pt"],"allowDifferentDisplayLocales":false}""";
        if (previouslySaved)
        {
            using var saved = await PutAsync(browser, "preferences", Body, "0");
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var version = previouslySaved ? "1" : "0";
        var responses = await Task.WhenAll(PutAsync(browser, "preferences", Body, version), PutAsync(browser, "preferences", Body, version));
        try
        { Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(x => x.StatusCode).Order()); }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("""{"communicationLanguages":[],"allowDifferentDisplayLocales":false}""")]
    [InlineData("""{"communicationLanguages":["pt","pt"],"allowDifferentDisplayLocales":false}""")]
    [InlineData("""{"communicationLanguages":["en"]}""")]
    [InlineData("""{"communicationLanguages":["en-US"],"allowDifferentDisplayLocales":true}""")]
    [InlineData("""{"communicationLanguages":["en"],"allowDifferentDisplayLocales":true,"userId":"other"}""")]
    public async Task InvalidDeclarationIsRejected(string body)
    {
        using var browser = await BrowserAsync();
        using var response = await PutAsync(browser, "preferences", body, "0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocaleEditDoesNotDeclareLanguagesAndIsConditional()
    {
        using var browser = await BrowserAsync();
        using var saved = await PutAsync(browser, "locale", """{"locale":"pt-BR"}""", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var stale = await PutAsync(browser, "locale", """{"locale":"en-US"}""", "0");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var preferences = await browser.GetAsync("/api/v1/me/preferences");
        Assert.Equal("\"0\"", preferences.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task MissingPreconditionAndCsrfAreRejected()
    {
        using var browser = await BrowserAsync();
        const string Body = """{"communicationLanguages":["en"],"allowDifferentDisplayLocales":false}""";
        using var missing = await browser.PutAsync("/api/v1/me/preferences", Body);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var csrf = await browser.PutAsync("/api/v1/me/preferences", Body,
            new MutationHeaders(SendCsrfToken: false, Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task OtherAccountCannotReadOwnersDeclaration()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var owner = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(owner);
        using var saved = await PutAsync(browser, "preferences",
            """{"communicationLanguages":["en","pt"],"allowDifferentDisplayLocales":true}""", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var other = await BrowserAsync();
        using var read = await other.GetAsync($"/api/v1/me/preferences?userId={owner.UserId}");
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Empty(body["communicationLanguages"]!.AsArray());
        Assert.True((bool)body["declarationRequired"]!);
        using var byId = await other.GetAsync($"/api/v1/me/preferences/{owner.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task LocaleAndPreferencesIncrementGuardWithoutChangingRegion()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var profile = await browser.PatchRawJsonAsync("/api/v1/me/profile", """{"regionCode":"BR-SP"}""", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var preferences = await PutAsync(browser, "preferences", """{"communicationLanguages":["en"],"allowDifferentDisplayLocales":false}""", "0");
        using var locale = await PutAsync(browser, "locale", """{"locale":"pt-BR"}""", "0");
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        Assert.Equal(HttpStatusCode.OK, locale.StatusCode);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select security_version from safety.user_guards where user_id = $1", connection);
        command.Parameters.AddWithValue(account.UserId);
        Assert.Equal(2L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var read = await browser.GetAsync("/api/v1/me/profile");
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("BR-SP", (string?)body["regionCode"]);
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    [InlineData("DeletionPending")]
    public async Task RestrictedAccountCannotChangeDeclarationsOrLocale(string status)
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("update identity.users set status = $1 where id = $2", connection);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(account.UserId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var preferences = await PutAsync(browser, "preferences", """{"communicationLanguages":["en"],"allowDifferentDisplayLocales":false}""", "0");
        using var locale = await PutAsync(browser, "locale", """{"locale":"pt-BR"}""", "0");
        Assert.Equal(HttpStatusCode.Forbidden, preferences.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, locale.StatusCode);
    }

    private async Task<Browser> BrowserAsync()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        return await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
    }

    private static Task<HttpResponseMessage> PutAsync(Browser browser, string resource, string body, string version) =>
        browser.PutAsync($"/api/v1/me/{resource}", body, new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" }));
}
