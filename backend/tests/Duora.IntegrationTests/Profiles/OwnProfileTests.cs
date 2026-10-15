using System.Net;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

/// <summary>
/// API-P01 e API-P02: leitura e edição do próprio perfil com concorrência otimista (spec 8.1: ETag + If-Match,
/// ausência → 428, conflito → 412). Todo caso de recusa confere também que nada foi gravado.
/// </summary>
public sealed class OwnProfileTests : IAsyncDisposable
{
    private const string ProfilePath = "/api/v1/me/profile";

    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;

    public OwnProfileTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task UnsavedProfileReadsAsEmptyVersionZeroWithoutCreatingARow()
    {
        var (account, browser) = await SignedInAsync();
        using var _ = browser;

        using var response = await browser.GetAsync(ProfilePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ReadObjectAsync(response);
        Assert.Equal(["bio", "displayName", "version"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Null(body["displayName"]);
        Assert.Null(body["bio"]);
        Assert.Equal("0", (string?)body["version"]);
        Assert.Equal("none", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task FirstSaveWithETagZeroCreatesVersionOne()
    {
        var (account, browser) = await SignedInAsync();
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":"Ana","bio":"Gosto de jogos."}""", "\"0\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        var body = await ReadObjectAsync(response);
        Assert.Equal("Ana", (string?)body["displayName"]);
        Assert.Equal("Gosto de jogos.", (string?)body["bio"]);
        Assert.Equal("1", (string?)body["version"]);
        Assert.Equal("Ana|Gosto de jogos.|1", await StoredAsync(account.UserId));

        using var read = await browser.GetAsync(ProfilePath);
        Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        Assert.Equal("Ana", (string?)(await ReadObjectAsync(read))["displayName"]);
    }

    [Fact]
    public async Task AbsentFieldIsKeptAndOnlyTheSentFieldChanges()
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Gosto de jogos.");
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":"Bia"}""", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
        Assert.Equal("Bia|Gosto de jogos.|2", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task NullBioClearsTheDescription()
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Gosto de jogos.");
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"bio":null}""", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await ReadObjectAsync(response))["bio"]);
        Assert.Equal("Ana|<null>|2", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task NullDisplayNameIsRejectedInsteadOfIgnored()
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":null}""", "\"1\"");

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("NULL_NOT_ALLOWED", (string?)problem["errors"]?["displayName"]?[0]);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task EmptyEditWithTheCurrentETagChangesNothing()
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, "{}", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public async Task PatchWithoutASpecificETagIs428AndWritesNothing(string? ifMatch)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":"Bia"}""", ifMatch);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    /// <summary>T-19: a aba A salva; a aba B, com a versão que leu antes, recebe 412 e a alteração de A fica.</summary>
    [Fact]
    [Trait("Acceptance", "T-19")]
    public async Task StaleETagFromAnotherTabIs412AndKeepsTheFirstSave()
    {
        var (account, tabA) = await SavedProfileAsync("Ana", "Oi");
        using var _ = tabA;
        using var tabB = await Scenario.SignedInBrowserAsync(account);
        using var readByB = await tabB.GetAsync(ProfilePath);
        var etagSeenByB = readByB.Headers.ETag?.ToString();

        using (var savedByA = await PatchAsync(tabA, """{"displayName":"Ana Souza"}""", "\"1\""))
        {
            Assert.Equal(HttpStatusCode.OK, savedByA.StatusCode);
        }

        using var savedByB = await PatchAsync(tabB, """{"displayName":"Bia"}""", etagSeenByB);

        await ProblemAssert.IsProblemAsync(savedByB, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Ana Souza|Oi|2", await StoredAsync(account.UserId));
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"2\"")]
    [InlineData("W/\"1\"")]
    [InlineData("1")]
    [InlineData("\"abc\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"99999999999999999999\"")]
    [InlineData("\"1\" ; drop")]
    public async Task ETagThatDoesNotMatchTheCurrentVersionIs412(string ifMatch)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":"Bia"}""", ifMatch);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    /// <summary>T-19 sob concorrência real: duas gravações com a mesma versão, só uma vence (INSERT e UPDATE).</summary>
    [Theory]
    [Trait("Acceptance", "T-19")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSavesWithTheSameETagHaveExactlyOneWinner(bool alreadySaved)
    {
        var (account, first) = alreadySaved ? await SavedProfileAsync("Ana", "Oi") : await SignedInAsync();
        using var _ = first;
        using var second = await Scenario.SignedInBrowserAsync(account);
        var etag = alreadySaved ? "\"1\"" : "\"0\"";

        var responses = await Task.WhenAll(
            PatchAsync(first, """{"displayName":"Primeira"}""", etag),
            PatchAsync(second, """{"displayName":"Segunda"}""", etag));

        try
        {
            var statuses = responses.Select(r => r.StatusCode).Order().ToArray();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], statuses);
            var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
            var winnerName = (string?)(await ReadObjectAsync(winner))["displayName"];
            var expectedVersion = alreadySaved ? 2 : 1;
            var expectedBio = alreadySaved ? "Oi" : "<null>";
            Assert.Equal($"{winnerName}|{expectedBio}|{expectedVersion}", await StoredAsync(account.UserId));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>Overposting (spec 8.2, API-P02): id, dono, versão e estado nunca vêm do cliente.</summary>
    [Theory]
    [InlineData("""{"displayName":"Bia","userId":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"displayName":"Bia","ownerId":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"displayName":"Bia","version":"7"}""")]
    [InlineData("""{"displayName":"Bia","status":"Active"}""")]
    [InlineData("""{"displayName":"Bia","ageBand":"18-24"}""")]
    [InlineData("""{"displayName":"Bia","DisplayName":"Outra"}""")]
    [InlineData("""{"displayName":"Bia","displayName":"Outra"}""")]
    public async Task UnknownOrRepeatedFieldIs400AndWritesNothing(string json)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, json, "\"1\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Theory]
    [InlineData("""{"displayName":123}""")]
    [InlineData("""{"bio":["a"]}""")]
    [InlineData("""{"displayName":{"value":"Bia"}}""")]
    [InlineData("""{"displayName":"Bia" """)]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"displayName":"Ana\uD800"}""")]
    public async Task MalformedBodyIs400AndWritesNothing(string json)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, json, "\"1\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Theory]
    [InlineData("displayName", "\"\"", "REQUIRED")]
    [InlineData("displayName", "\"   \"", "REQUIRED")]
    [InlineData("displayName", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "TOO_LONG")]
    [InlineData("displayName", "\"Ana\\u0000\"", "INVALID_CHARACTERS")]
    [InlineData("displayName", "\"Ana\\u0007\"", "INVALID_CHARACTERS")]
    [InlineData("displayName", "\"Ana\\nSouza\"", "INVALID_CHARACTERS")]
    [InlineData("displayName", "\"Ana\\u202Eazuos\"", "INVALID_CHARACTERS")]
    [InlineData("displayName", "\"Ana\\u200BSouza\"", "INVALID_CHARACTERS")]
    [InlineData("displayName", "\"Ana\\uFEFF\"", "INVALID_CHARACTERS")]
    [InlineData("bio", "\"Oi\\u200Dtudo\"", "INVALID_CHARACTERS")]
    [InlineData("bio", "\"\"", "REQUIRED")]
    [InlineData("bio", "\"Oi\\u0000\"", "INVALID_CHARACTERS")]
    [InlineData("bio", "\"Oi\\u001B[31m\"", "INVALID_CHARACTERS")]
    public async Task InvalidTextIs400WithTheFieldCodeAndWritesNothing(string field, string jsonValue, string code)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, $$"""{"{{field}}":{{jsonValue}}}""", "\"1\"");

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(code, (string?)problem["errors"]?[field]?[0]);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task BioAboveTheLimitIs400()
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;

        using var response = await PatchAsync(browser, $$"""{"bio":"{{new string('a', 501)}}"}""", "\"1\"");

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("TOO_LONG", (string?)problem["errors"]?["bio"]?[0]);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task ValuesAtTheLimitsAreAccepted()
    {
        var (account, browser) = await SignedInAsync();
        using var _ = browser;
        // 40 emojis (80 unidades UTF-16) e 500 letras: o limite conta caracteres, igual ao char_length do banco.
        var name = string.Concat(Enumerable.Repeat("😀", 40));
        var bio = new string('b', 500);

        using var response = await PatchAsync(browser, $$"""{"displayName":"{{name}}","bio":"{{bio}}"}""", "\"0\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{name}|{bio}|1", await StoredAsync(account.UserId));
    }

    /// <summary>O nome decomposto (e + acento combinante) é gravado na forma composta, igual ao digitado em NFC.</summary>
    [Fact]
    public async Task TextIsStoredInNfc()
    {
        var (account, browser) = await SignedInAsync();
        using var _ = browser;

        using var response = await PatchAsync(browser, """{"displayName":"Jose\u0301"}""", "\"0\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Jos\u00E9|<null>|1", await StoredAsync(account.UserId));
    }

    [Fact]
    public async Task SqlLookingTextIsStoredAsPlainText()
    {
        var (account, browser) = await SignedInAsync();
        using var _ = browser;
        const string Name = "'); drop table profiles.profiles; --";

        using var response = await PatchAsync(browser, $$"""{"displayName":"{{Name}}"}""", "\"0\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{Name}|<null>|1", await StoredAsync(account.UserId));
    }

    /// <summary>
    /// Só o próprio perfil: não há rota com id de outra pessoa, a leitura de B não traz dados de A, o id de A em query
    /// ou caminho não muda o alvo, e o ETag de A não serve para gravar em A.
    /// </summary>
    [Fact]
    public async Task AnotherAccountNeverReadsOrChangesTheProfile()
    {
        var (owner, ownerBrowser) = await SavedProfileAsync("Ana Canário", "Descrição da Ana");
        using var _ = ownerBrowser;
        var (intruder, intruderBrowser) = await SignedInAsync();
        using var __ = intruderBrowser;

        using var ownRead = await intruderBrowser.GetAsync($"{ProfilePath}?userId={owner.UserId}");
        using var byId = await intruderBrowser.GetAsync($"/api/v1/profiles/{owner.UserId}");
        using var byMePath = await intruderBrowser.GetAsync($"{ProfilePath}/{owner.UserId}");
        using var write = await PatchAsync(intruderBrowser, """{"displayName":"Intrusa"}""", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        Assert.Equal("\"0\"", ownRead.Headers.ETag?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byMePath.StatusCode);
        foreach (var response in new[] { ownRead, byId, byMePath, write })
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Canário", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Descrição da Ana", text, StringComparison.Ordinal);
        }

        await ProblemAssert.IsProblemAsync(write, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal("Ana Canário|Descrição da Ana|1", await StoredAsync(owner.UserId));
        Assert.Equal("none", await StoredAsync(intruder.UserId));
    }

    /// <summary>Spec 8.2: conta restrita, suspensa ou em exclusão lê o próprio perfil, mas não o altera.</summary>
    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    [InlineData("DeletionPending")]
    public async Task AccountOutOfGoodStandingReadsButCannotEdit(string status)
    {
        var (account, browser) = await SavedProfileAsync("Ana", "Oi");
        using var _ = browser;
        await SetStatusAsync(account.UserId, status);

        using var write = await PatchAsync(browser, """{"displayName":"Bia"}""", "\"1\"");
        using var read = await browser.GetAsync(ProfilePath);

        await ProblemAssert.IsProblemAsync(write, HttpStatusCode.Forbidden, "ACCOUNT_RESTRICTED");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("Ana|Oi|1", await StoredAsync(account.UserId));
    }

    /// <summary>O texto do perfil não volta em erro nem vai para o log (pode conter dado pessoal, data-map.md).</summary>
    [Fact]
    public async Task ProfileTextIsNeitherEchoedInErrorsNorLogged()
    {
        var (_, browser) = await SignedInAsync();
        using var __ = browser;
        var canary = $"CANARY-{Guid.NewGuid():N}";

        using var rejected = await PatchAsync(browser, $$"""{"displayName":"{{canary}}{{new string('x', 40)}}"}""", "\"0\"");
        using var saved = await PatchAsync(browser, $$"""{"bio":"{{canary}}"}""", "\"0\"");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain(canary, await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(canary, StringComparison.Ordinal));
    }

    private async Task<(TestAccount Account, Browser Browser)> SignedInAsync()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        return (account, await Scenario.SignedInBrowserAsync(account));
    }

    private async Task<(TestAccount Account, Browser Browser)> SavedProfileAsync(string displayName, string bio)
    {
        var (account, browser) = await SignedInAsync();
        var body = new JsonObject { ["displayName"] = displayName, ["bio"] = bio }.ToJsonString();
        using var response = await PatchAsync(browser, body, "\"0\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (account, browser);
    }

    private static Task<HttpResponseMessage> PatchAsync(Browser browser, string json, string? ifMatch) =>
        browser.PatchRawJsonAsync(ProfilePath, json, ifMatch);

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Corpo não é objeto.");
    }

    /// <summary>Linha gravada como "nome|descrição|versão", com <c>&lt;null&gt;</c> para coluna nula; "none" sem linha.</summary>
    private async Task<string> StoredAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(display_name, '<null>') || '|' || coalesce(bio, '<null>') || '|' || version
            from profiles.profiles where user_id = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string ?? "none";
    }

    /// <summary>Estado de conta que ainda não tem fluxo pela API (sanção do Safety); dado sintético do teste.</summary>
    private async Task SetStatusAsync(Guid userId, string status)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("update identity.users set status = $1 where id = $2", connection);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(userId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
