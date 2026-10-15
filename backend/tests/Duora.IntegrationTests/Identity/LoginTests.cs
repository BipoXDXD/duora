using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>Login (API-A04): respostas neutras, MFA como contrato, bloqueio silencioso e rate limit.</summary>
public sealed class LoginTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task WrongPasswordAndUnknownEmailGetTheSameResponse()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);

        using var wrongPassword = await IdentityScenario.LoginAsync(browser, account.Email, "senha errada mas longa");
        using var unknownEmail = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), "senha errada mas longa");

        await ProblemAssert.IsProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        await ProblemAssert.IsProblemAsync(unknownEmail, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongPassword),
            await ProblemAssert.BodyWithoutTraceIdAsync(unknownEmail));
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task UnconfirmedEmailGetsTheSameResponseEvenWithTheRightPassword()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        using var registered = await IdentityScenario.RegisterAsync(browser, email);

        using var unconfirmed = await IdentityScenario.LoginAsync(browser, email, IdentityScenario.ValidPassword);
        using var unknown = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), IdentityScenario.ValidPassword);

        await ProblemAssert.IsProblemAsync(unconfirmed, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(unknown),
            await ProblemAssert.BodyWithoutTraceIdAsync(unconfirmed));
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task AccountWithMfaGetsMfaRequiredAndNoSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.ScalarAsync("update identity.users set two_factor_enabled = true where id = $1 returning 1", account.UserId);
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "MFA_REQUIRED");
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task MfaIsOnlyRevealedAfterTheRightPassword()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.ScalarAsync("update identity.users set two_factor_enabled = true where id = $1 returning 1", account.UserId);
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, "senha errada mas longa");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task LockedOutAccountGetsTheSameResponseAsAWrongPassword()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.ScalarAsync(
            "update identity.users set lockout_end = now() + interval '1 hour' where id = $1 returning 1",
            account.UserId);
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("ana@example.com", "")]
    [InlineData("não é e-mail", "qualquer senha longa")]
    [InlineData("ana\u0000@example.com", "qualquer senha longa")]
    [InlineData("admin@example.com' OR '1'='1", "' OR '1'='1")]
    public async Task InvalidInputIsAClientError(string email, string password)
    {
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, email, password);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task PasswordAboveTheCeilingIsRejectedBeforeHashing()
    {
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), new string('x', 129));

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("PASSWORD_TOO_LONG", (string?)problem["errors"]?["password"]?[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedAttemptsAgainstOneAccountAreRateLimitedWhetherItExistsOrNot(bool accountExists)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            // Isola o limite por conta: o limite por IP fica alto só neste teste.
            Settings = new Dictionary<string, string?> { ["RateLimiting:LoginPerIp:PermitLimit"] = "1000" },
        };
        var email = accountExists
            ? (await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync()).Email
            : IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var allowed = await IdentityScenario.LoginAsync(browser, email, "senha errada mas longa");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await IdentityScenario.LoginAsync(browser, email, "senha errada mas longa");

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
    }

    [Fact]
    public async Task RepeatedAttemptsFromOneAddressAreRateLimited()
    {
        using var browser = new Browser(_factory);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var allowed = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), "senha errada mas longa");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await IdentityScenario.LoginAsync(browser, IdentityScenario.NewEmail(), "senha errada mas longa");

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
    }
}
