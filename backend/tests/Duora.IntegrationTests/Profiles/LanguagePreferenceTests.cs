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

    /// <summary>
    /// T-19 em API-P03: duas abas leem a mesma versão e gravam uma depois da outra, com corpos diferentes. A aba antiga
    /// recebe <c>412</c> e a primeira gravação fica no banco, sem perda silenciosa.
    /// </summary>
    [Theory]
    [InlineData(false, "1|true|pt")]
    [InlineData(true, "2|true|pt")]
    public async Task StaleTabGets412AfterAnotherTabSavedAndTheFirstSaveStays(bool previouslySaved, string expectedRow)
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        if (previouslySaved)
        {
            using var saved = await PutAsync(browser, "preferences", """{"communicationLanguages":["en"],"allowDifferentDisplayLocales":false}""", "0");
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        using var tabA = await browser.GetAsync("/api/v1/me/preferences");
        using var tabB = await browser.GetAsync("/api/v1/me/preferences");
        var tag = tabA.Headers.ETag!.Tag;
        Assert.Equal(tag, tabB.Headers.ETag!.Tag);

        using var first = await PutWithTagAsync(browser, """{"communicationLanguages":["pt"],"allowDifferentDisplayLocales":true}""", tag);
        using var stale = await PutWithTagAsync(browser, """{"communicationLanguages":["en","pt"],"allowDifferentDisplayLocales":false}""", tag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = JsonNode.Parse(await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("PRECONDITION_FAILED", (string?)problem["code"]);
        Assert.Equal(expectedRow, await StoredPreferencesAsync(account.UserId));
        using var reread = await browser.GetAsync("/api/v1/me/preferences");
        Assert.Equal(first.Headers.ETag!.Tag, reread.Headers.ETag!.Tag);
        var body = JsonNode.Parse(await reread.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(["pt"], body["communicationLanguages"]!.AsArray().Select(x => (string?)x));
        Assert.True((bool)body["allowDifferentDisplayLocales"]!);
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
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select security_version from safety.user_guards where user_id = $1", connection);
        command.Parameters.AddWithValue(account.UserId);
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var preferences = await PutAsync(browser, "preferences", """{"communicationLanguages":["en"],"allowDifferentDisplayLocales":false}""", "0");
        using var locale = await PutAsync(browser, "locale", """{"locale":"pt-BR"}""", "0");
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        Assert.Equal(HttpStatusCode.OK, locale.StatusCode);
        Assert.Equal(3L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task RegistrationAttemptCannotOverwriteConfirmedAccountsLocale()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var saved = await PutAsync(browser, "locale", """{"locale":"pt-BR"}""", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var stranger = new Browser(_factory);
        using var attempted = await IdentityScenario.RegisterAsync(stranger, account.Email);
        Assert.Equal(HttpStatusCode.Accepted, attempted.StatusCode);
        using var read = await browser.GetAsync("/api/v1/me/locale");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("pt-BR", (string?)body["locale"]);
        Assert.Equal("1", (string?)body["version"]);
    }

    private async Task<Browser> BrowserAsync()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        return await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
    }

    private static Task<HttpResponseMessage> PutAsync(Browser browser, string resource, string body, string version) =>
        browser.PutAsync($"/api/v1/me/{resource}", body, new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" }));

    private static Task<HttpResponseMessage> PutWithTagAsync(Browser browser, string body, string entityTag) =>
        browser.PutAsync("/api/v1/me/preferences", body, new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = entityTag }));

    /// <summary><c>versão|permite outro idioma de exibição|idiomas em ordem</c>, lido direto do banco.</summary>
    private async Task<string> StoredPreferencesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            select p.version||'|'||p.allow_different_display_locales||'|'||
              array_to_string(array(select language_code from profiles.communication_languages where user_id=p.user_id order by language_code),',')
            from profiles.preferences p where p.user_id=$1
            """, connection);
        command.Parameters.AddWithValue(userId);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
