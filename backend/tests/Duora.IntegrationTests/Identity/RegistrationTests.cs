using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Duora.IntegrationTests.Identity;

/// <summary>Cadastro e confirmação de e-mail (API-A02, API-A03): resposta neutra, DTO estrito e limites.</summary>
public sealed class RegistrationTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task NewAccountIsPendingUntilTheEmailIsConfirmed()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();

        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        using var beforeConfirmation = await IdentityScenario.LoginAsync(browser, email, IdentityScenario.ValidPassword);
        var link = await Scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        using var confirmed = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new { userId = link.UserId, token = link.Token, password = IdentityScenario.ValidPassword });
        using var afterConfirmation = await IdentityScenario.LoginAsync(browser, email, IdentityScenario.ValidPassword);

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        await ProblemAssert.IsProblemAsync(beforeConfirmation, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, afterConfirmation.StatusCode);
        Assert.Equal(2, await Scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1",
            link.UserId));
    }

    [Fact]
    public async Task RegistrationIsIndistinguishableForAnEmailThatAlreadyHasAnAccount()
    {
        var existing = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);

        using var forExisting = await IdentityScenario.RegisterAsync(browser, existing.Email, "outra frase longa qualquer");
        using var forNew = await IdentityScenario.RegisterAsync(browser, IdentityScenario.NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, forExisting.StatusCode);
        Assert.Equal(forNew.StatusCode, forExisting.StatusCode);
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(forNew),
            await ProblemAssert.BodyWithoutTraceIdAsync(forExisting));
        Assert.Equal(1, await Scenario.CountUsersAsync(existing.Email));
        Assert.NotNull(await Scenario.TryReadLatestLinkAsync(existing.Email, "RegistrationAttemptOnExistingAccount"));
    }

    [Fact]
    public async Task ConcurrentRegistrationsOfTheSameEmailCreateOneAccount()
    {
        var email = IdentityScenario.NewEmail();
        var browsers = Enumerable.Range(0, 3).Select(_ => new Browser(_factory)).ToArray();

        var responses = await Task.WhenAll(browsers.Select(browser => IdentityScenario.RegisterAsync(browser, email)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(1, await Scenario.CountUsersAsync(email));
        foreach (var disposable in responses.Cast<IDisposable>().Concat(browsers))
        {
            disposable.Dispose();
        }
    }

    [Theory]
    [InlineData("status", "\"Active\"")]
    [InlineData("role", "\"Admin\"")]
    [InlineData("emailConfirmed", "true")]
    [InlineData("id", "\"0199d700-7f20-7000-8000-000000000001\"")]
    [InlineData("age", "30")]
    [InlineData("birthDate", "\"1990-01-01\"")]
    public async Task UnknownFieldIsRejectedAndNothingIsCreated(string field, string value)
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();

        using var response = await browser.PostRawJsonAsync(
            "/api/v1/auth/register",
            $$"""
            {"email":"{{email}}","password":"{{IdentityScenario.ValidPassword}}",
             "acceptedTermsVersion":"v1","acceptedPrivacyVersion":"v1","{{field}}":{{value}}}
            """);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(0, await Scenario.CountUsersAsync(email));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(email));
    }

    // Issue #9, item 5: the 18+ declaration is a product requirement of the Terms, not age verification. Without an
    // explicit true the request fails at the boundary and writes no account, consent or outbox row.
    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task RegistrationWithoutTheAdultDeclarationIsRejectedAndNothingIsStored(string? confirmedAdult)
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();

        using var response = await browser.PostRawJsonAsync("/api/v1/auth/register", RegistrationJson(email, confirmedAdult));

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("REQUIRED", (string?)problem["errors"]?["confirmedAdult"]?[0]);
        Assert.Single(problem["errors"]!.AsObject());
        Assert.Equal(0, await Scenario.CountUsersAsync(email));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(email));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public async Task AdultDeclarationOfTheWrongTypeIsRejectedAndNothingIsStored(string confirmedAdult)
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();

        using var response = await browser.PostRawJsonAsync("/api/v1/auth/register", RegistrationJson(email, confirmedAdult));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(0, await Scenario.CountUsersAsync(email));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(email));
    }

    [Fact]
    public async Task ReRegistrationWithoutTheAdultDeclarationLeavesThePendingAccountUntouched()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        using var first = await IdentityScenario.RegisterAsync(browser, email);
        const string Evidence = """
            select string_agg(c.document || '|' || c.version || '|' || c.accepted_at::text || '|' || u.password_hash, ',' order by c.document)
            from identity.consent_records c join identity.users u on u.id = c.user_id
            where u.normalized_email = upper($1)
            """;
        var before = await Scenario.ScalarTextAsync(Evidence, email);

        using var second = await browser.PostRawJsonAsync("/api/v1/auth/register", RegistrationJson(email, "false", "outra frase longa o bastante"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await ProblemAssert.IsProblemAsync(second, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(before, await Scenario.ScalarTextAsync(Evidence, email));
        Assert.Equal(1, await Scenario.CountOutboxEventsAsync(email));
    }

    /// <summary>A valid registration body whose <c>confirmedAdult</c> is replaced by raw JSON, or omitted when null.</summary>
    private static string RegistrationJson(string email, string? confirmedAdult, string password = IdentityScenario.ValidPassword)
    {
        var body = JsonSerializer.SerializeToNode(IdentityScenario.RegistrationBody(email, password))!.AsObject();
        body.Remove("confirmedAdult");
        if (confirmedAdult is not null)
        {
            body["confirmedAdult"] = JsonNode.Parse(confirmedAdult);
        }
        return body.ToJsonString();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"email\":\"a@b.co\",\"email\":\"c@d.co\"}")]
    public async Task MalformedBodyIsABadRequestProblem(string json)
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostRawJsonAsync("/api/v1/auth/register", json);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
    }

    [Theory]
    [InlineData(11, "PASSWORD_TOO_SHORT")]
    [InlineData(129, "PASSWORD_TOO_LONG")]
    public async Task PasswordOutsideTwelveToOneHundredTwentyEightIsRejected(int length, string code)
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();

        using var response = await IdentityScenario.RegisterAsync(browser, email, new string('x', length));

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(code, (string?)problem["errors"]?["password"]?[0]);
        Assert.Equal(0, await Scenario.CountUsersAsync(email));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    public async Task PasswordOnTheBoundariesIsAccepted(int length)
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();

        using var response = await IdentityScenario.RegisterAsync(browser, email, new string('x', length));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await Scenario.CountUsersAsync(email));
    }

    [Fact]
    public async Task EmailOf254CharactersIsAcceptedAnd255IsRejected()
    {
        using var browser = new Browser(_factory);
        var atLimit = $"{Guid.NewGuid():N}{new string('a', 32)}@{new string('b', 185)}.com";
        var aboveLimit = "c" + atLimit;

        using var accepted = await IdentityScenario.RegisterAsync(browser, atLimit);
        using var rejected = await IdentityScenario.RegisterAsync(browser, aboveLimit);

        Assert.Equal(254, atLimit.Length);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await ProblemAssert.IsProblemAsync(rejected, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("ana\u0000@example.com", IdentityScenario.ValidPassword)]
    [InlineData("ana@example.com", "frase longa\u0000 o bastante")]
    [InlineData("' OR 1=1 --@example.com", IdentityScenario.ValidPassword)]
    [InlineData("ana@example.com'; drop table identity.users; --", IdentityScenario.ValidPassword)]
    public async Task HostileInputIsAClientErrorNeverAServerError(string email, string password)
    {
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.RegisterAsync(browser, email, password);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task DocumentVersionsAreValidated()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync("/api/v1/auth/register", new
        {
            email = IdentityScenario.NewEmail(),
            password = IdentityScenario.ValidPassword,
            acceptedTermsVersion = new string('1', 33),
            acceptedPrivacyVersion = "v1 <script>",
        });

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("TOO_LONG", (string?)problem["errors"]?["acceptedTermsVersion"]?[0]);
        Assert.Equal("INVALID_FORMAT", (string?)problem["errors"]?["acceptedPrivacyVersion"]?[0]);
    }

    [Theory]
    [InlineData("nao-e-um-token")]
    [InlineData("%%%")]
    [InlineData("tok\u0000en")]
    public async Task InvalidConfirmationTokenIsRejected(string token)
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        var link = await Scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        using var response = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new { userId = link.UserId, token, password = IdentityScenario.ValidPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and email_confirmed",
            link.UserId));
    }

    [Fact]
    public async Task ConfirmationForUnknownAccountLooksLikeAnInvalidToken()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        using var registered = await IdentityScenario.RegisterAsync(browser, email);
        var link = await Scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        using var wrongToken = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new { userId = link.UserId, token = "nope", password = IdentityScenario.ValidPassword });
        using var unknownUser = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new { userId = Guid.NewGuid(), token = link.Token, password = IdentityScenario.ValidPassword });

        await ProblemAssert.IsProblemAsync(wrongToken, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        await ProblemAssert.IsProblemAsync(unknownUser, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongToken),
            await ProblemAssert.BodyWithoutTraceIdAsync(unknownUser));
    }

    [Fact]
    public async Task RepeatedRegistrationOfTheSameEmailIsRateLimited()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var allowed = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        using var limited = await IdentityScenario.RegisterAsync(browser, email);

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
    }
}
