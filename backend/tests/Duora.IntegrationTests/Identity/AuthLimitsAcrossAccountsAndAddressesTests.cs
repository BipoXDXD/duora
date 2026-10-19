using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Limites das rotas de autenticação na matriz contas × IPs (spec 11.5): cada operação conta por conta-alvo e por
/// IP, trocar de IP não renova a cota da conta nem destrava o bloqueio, várias contas atrás de um IP dividem a cota
/// do IP, e esgotar a cota de uma conta ou de um IP não atinge outra conta noutro IP.
/// </summary>
[Trait("Threat", "TM-01")]
public sealed class AuthLimitsAcrossAccountsAndAddressesTests(PostgresFixture postgres)
{
    private const string WrongPassword = "senha errada mas longa";
    private const string PasswordResets = "/api/v1/auth/password-resets";
    private const string EmailConfirmations = "/api/v1/auth/confirm-email";
    private const string PasswordChanges = "/api/v1/me/password-changes";
    private const string Sessions = "/api/v1/me/sessions";

    /// <summary>Uma cota de IP pequena, para a matriz caber em poucos pedidos.</summary>
    private const int AddressQuota = 3;

    private const string SharedAddress = "203.0.113.200";

    /// <summary>Teto padrão do bloqueio do Identity (AccountServiceCollectionExtensions).</summary>
    private const int LockoutThreshold = 20;

    [Fact]
    public async Task LoginAttemptsAgainstOneAccountAreLimitedEvenWhenEachComesFromAnotherAddress()
    {
        await using var factory = Factory();
        var scenario = new IdentityScenario(postgres, factory);
        var target = await scenario.CreateConfirmedAccountAsync();
        var bystander = await scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(factory);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var allowed = await LoginFromAsync(browser, target.Email, WrongPassword, Address(attempt));
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await LoginFromAsync(browser, target.Email, target.Password, Address(10));
        using var other = await LoginFromAsync(browser, bystander.Email, bystander.Password, Address(11));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task TheAccountLocksOutEvenWhenEachFailureComesFromAnotherAddress()
    {
        // Só o bloqueio do banco: a cota por conta em memória fica alta para não responder antes dele.
        await using var factory = Factory(new() { ["RateLimiting:LoginPerAccount:PermitLimit"] = "1000" });
        var scenario = new IdentityScenario(postgres, factory);
        var target = await scenario.CreateConfirmedAccountAsync();
        var bystander = await scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(factory);
        for (var attempt = 0; attempt < LockoutThreshold; attempt++)
        {
            using var failed = await LoginFromAsync(browser, target.Email, WrongPassword, Address(attempt));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var rightPassword = await LoginFromAsync(browser, target.Email, target.Password, Address(LockoutThreshold));
        Assert.Null(browser.Cookie("__Host-duora"));
        using var otherBrowser = new Browser(factory);
        using var other = await LoginFromAsync(otherBrowser, bystander.Email, bystander.Password, Address(LockoutThreshold + 1));

        await ProblemAssert.IsProblemAsync(rightPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and lockout_end > now()", target.UserId));
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task AccountSecurityOperationsFollowTheAccountAcrossSessionsAndAddresses()
    {
        await using var factory = Factory();
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var first = await scenario.SignedInBrowserAsync(account);
        using var second = await scenario.SignedInBrowserAsync(account);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var browser = attempt % 2 == 0 ? first : second;
            using var allowed = await browser.PostJsonAsync(
                MfaScenario.Reauthenticate, new { password = WrongPassword }, RemoteAddressFromTestHeader.From(Address(attempt)));
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await second.PostJsonAsync(
            MfaScenario.Reauthenticate, new { password = account.Password }, RemoteAddressFromTestHeader.From(Address(10)));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
    }

    [Theory]
    [InlineData("POST", MfaScenario.Reauthenticate)]
    [InlineData("POST", MfaScenario.Enrollments)]
    [InlineData("POST", MfaScenario.Confirmations)]
    [InlineData("POST", MfaScenario.RecoveryCodes)]
    [InlineData("DELETE", MfaScenario.Mfa)]
    [InlineData("POST", PasswordChanges)]
    [InlineData("DELETE", Sessions + "/00000000-0000-7000-8000-000000000001")]
    public async Task ManyAccountsBehindOneAddressShareItsAccountSecurityQuota(string method, string path)
    {
        await using var factory = Factory(new() { ["RateLimiting:AccountSecurityPerIp:PermitLimit"] = $"{AddressQuota}" });
        var scenario = new IdentityScenario(postgres, factory);

        for (var account = 0; account < AddressQuota; account++)
        {
            var holder = await scenario.CreateConfirmedAccountAsync();
            using var signedIn = await scenario.SignedInBrowserAsync(holder);
            using var allowed = await signedIn.PostJsonAsync(
                MfaScenario.Reauthenticate, new { password = holder.Password }, RemoteAddressFromTestHeader.From(SharedAddress));
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        }

        var next = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(next);
        using var limited = await SendAsync(browser, method, path, next, SharedAddress);
        using var elsewhere = await browser.PostJsonAsync(
            MfaScenario.Reauthenticate, new { password = next.Password }, RemoteAddressFromTestHeader.From("198.51.100.200"));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        Assert.Equal(HttpStatusCode.NoContent, elsewhere.StatusCode);
    }

    [Theory]
    [InlineData(PasswordResets, true)]
    [InlineData(PasswordResets, false)]
    [InlineData(EmailConfirmations, true)]
    [InlineData(EmailConfirmations, false)]
    public async Task TokenRedemptionsForOneAccountAreLimitedEvenWhenEachComesFromAnotherAddress(string path, bool exists)
    {
        // O 429 vem igual para conta existente e inexistente: a cota é pelo id do corpo, não pelo cadastro. As contas
        // nascem noutra instância: a confirmação do cadastro já gastaria uma vez da cota (A03 e A07 somam).
        Guid otherUserId;
        Guid userId;
        await using (var setup = new DuoraWebFactory(postgres.ConnectionString))
        {
            var scenario = new IdentityScenario(postgres, setup);
            userId = exists ? (await scenario.CreateConfirmedAccountAsync()).UserId : Guid.NewGuid();
            otherUserId = (await scenario.CreateConfirmedAccountAsync()).UserId;
        }

        await using var factory = Factory(new() { ["RateLimiting:TokenRedemptionPerAccount:PermitLimit"] = $"{AddressQuota}" });
        using var browser = new Browser(factory);
        for (var attempt = 0; attempt < AddressQuota; attempt++)
        {
            using var rejected = await RedeemAsync(browser, path, userId, Address(attempt));
            await ProblemAssert.IsProblemAsync(rejected, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        }

        using var limited = await RedeemAsync(browser, path, userId, Address(AddressQuota));
        using var other = await RedeemAsync(browser, path, otherUserId, Address(AddressQuota));

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        await ProblemAssert.IsProblemAsync(other, HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    [Fact]
    public async Task SessionRevocationsAreLimitedPerAccountAcrossSessionsAndAddresses()
    {
        await using var factory = Factory(new() { ["RateLimiting:SessionRevocationPerAccount:PermitLimit"] = "2" });
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var first = await scenario.SignedInBrowserAsync(account);
        using var second = await scenario.SignedInBrowserAsync(account);

        using var one = await first.DeleteAsync($"{Sessions}/{Guid.NewGuid()}", RemoteAddressFromTestHeader.From(Address(0)));
        using var two = await second.DeleteAsync($"{Sessions}/{Guid.NewGuid()}", RemoteAddressFromTestHeader.From(Address(1)));
        using var limited = await first.DeleteAsync($"{Sessions}/{Guid.NewGuid()}", RemoteAddressFromTestHeader.From(Address(2)));
        using var otherOperation = await first.PostJsonAsync(
            MfaScenario.Reauthenticate, new { password = account.Password }, RemoteAddressFromTestHeader.From(Address(3)));

        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, two.StatusCode);
        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
        // Contador próprio: revogar sessões não gasta a cota de reautenticação e MFA.
        Assert.Equal(HttpStatusCode.NoContent, otherOperation.StatusCode);
    }

    private DuoraWebFactory Factory(Dictionary<string, string?>? settings = null) => new(postgres.ConnectionString)
    {
        Settings = settings ?? [],
        TestServices = RemoteAddressFromTestHeader.Register,
    };

    /// <summary>Um IP de documentação diferente por tentativa (RFC 5737).</summary>
    private static string Address(int index) => $"198.51.100.{index + 1}";

    private static async Task<HttpResponseMessage> LoginFromAsync(
        Browser browser, string email, string password, string address)
    {
        await EnsureCsrfTokenAsync(browser);
        return await browser.PostJsonAsync("/api/v1/auth/login", new { email, password }, RemoteAddressFromTestHeader.From(address));
    }

    private static async Task<HttpResponseMessage> RedeemAsync(Browser browser, string path, Guid userId, string address)
    {
        await EnsureCsrfTokenAsync(browser);
        object body = path == PasswordResets
            ? new { userId, token = "token-que-nao-vale", newPassword = "outra frase longa o bastante" }
            : new { userId, token = "token-que-nao-vale", password = IdentityScenario.ValidPassword };
        return await browser.PostJsonAsync(path, body, RemoteAddressFromTestHeader.From(address));
    }

    private static async Task EnsureCsrfTokenAsync(Browser browser)
    {
        if (browser.Cookie("XSRF-TOKEN") is null)
        {
            await browser.FetchCsrfTokenAsync();
        }
    }

    private static Task<HttpResponseMessage> SendAsync(
        Browser browser, string method, string path, TestAccount account, string address)
    {
        var headers = RemoteAddressFromTestHeader.From(address);
        return (method, path) switch
        {
            ("DELETE", _) => browser.DeleteAsync(path, headers),
            (_, MfaScenario.Reauthenticate) => browser.PostJsonAsync(path, new { password = account.Password }, headers),
            (_, MfaScenario.Confirmations) => browser.PostJsonAsync(path, new { code = "000000" }, headers),
            (_, PasswordChanges) => browser.PostJsonAsync(
                path, new { currentPassword = account.Password, newPassword = "outra frase longa o bastante" }, headers),
            _ => browser.PostJsonAsync(path, body: null, headers),
        };
    }
}
