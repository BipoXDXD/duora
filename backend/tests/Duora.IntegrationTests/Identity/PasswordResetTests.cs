using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Text;
using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

/// <summary>Recuperação de senha (API-A06, API-A07): pedido neutro, token de uso único e revogação das sessões.</summary>
public sealed class PasswordResetTests(PostgresFixture postgres) : IAsyncDisposable
{
    private const string NewPassword = "uma frase nova e longa";

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ResetRequestIsIndistinguishableForUnknownEmail()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var unknown = IdentityScenario.NewEmail();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var forExisting = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = account.Email });
        using var forUnknown = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = unknown });

        Assert.Equal(HttpStatusCode.Accepted, forExisting.StatusCode);
        Assert.Equal(forExisting.StatusCode, forUnknown.StatusCode);
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(forExisting),
            await ProblemAssert.BodyWithoutTraceIdAsync(forUnknown));
        Assert.NotNull(await Scenario.TryReadLatestLinkAsync(account.Email, "PasswordReset"));
        Assert.Empty(await Scenario.ReadLinksAsync(unknown, "PasswordReset"));
        Assert.Single(await Scenario.ReadLinksAsync(unknown, "PasswordResetForUnknownAddress"));
    }

    [Fact]
    public async Task T14ResetChangesThePasswordAndRevokesEverySession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var signedIn = await Scenario.SignedInBrowserAsync(account);
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = NewPassword });
        using var oldSession = await signedIn.GetAsync("/api/v1/me");
        using var oldPassword = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);
        using var newPassword = await IdentityScenario.LoginAsync(browser, account.Email, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await ProblemAssert.IsProblemAsync(oldSession, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await ProblemAssert.IsProblemAsync(oldPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, newPassword.StatusCode);
        Assert.Equal(1, await Scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and revocation_reason = 'password-reset'",
            account.UserId));
    }

    [Fact]
    public async Task ResetTokenCannotBeUsedTwice()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);

        using var first = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = NewPassword });
        using var second = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = "terceira frase bem longa" });
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await ProblemAssert.IsProblemAsync(second, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task InvalidTokenKeepsTheOldPassword()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = account.UserId, token = "token-falso-de-teste", newPassword = NewPassword });
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        await ProblemAssert.IsProblemAsync(reset, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task NewPasswordFollowsThePolicy()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = "curta" });

        var problem = await ProblemAssert.IsProblemAsync(reset, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("PASSWORD_TOO_SHORT", (string?)problem["errors"]?["newPassword"]?[0]);
    }

    [Fact]
    public async Task ResetClearsALockoutCausedByFailedAttempts()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.ScalarAsync(
            "update identity.users set lockout_end = now() + interval '1 hour', access_failed_count = 19 where id = $1 returning 1",
            account.UserId);
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = NewPassword });
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(31, false)]
    public async Task ResetLinkExpiresThirtyMinutesAfterItWasIssued(int ageMinutes, bool accepted)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var link = await RequestResetLinkAsync(browser, account.Email);
        var aged = AgeToken(link.Token, TimeSpan.FromMinutes(ageMinutes));

        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = aged, newPassword = NewPassword });
        using var withNewPassword = await IdentityScenario.LoginAsync(browser, account.Email, NewPassword);
        using var withOldPassword = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, withNewPassword.StatusCode);
            await ProblemAssert.IsProblemAsync(withOldPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        }
        else
        {
            await ProblemAssert.IsProblemAsync(reset, HttpStatusCode.BadRequest, "INVALID_TOKEN");
            await ProblemAssert.IsProblemAsync(withNewPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
            Assert.Equal(HttpStatusCode.NoContent, withOldPassword.StatusCode);
        }
    }

    [Fact]
    public void ResetTokenProviderIsConfiguredForThirtyMinutes()
    {
        // O tipo das opções é interno do Duora.Web.Identity; as duas leituras são o contrato de 30 minutos.
        var optionsType = typeof(CredentialVerifier).Assembly.GetType(
            "Duora.Web.Identity.PasswordResetTokenProviderOptions", throwOnError: true)!;
        var options = (IOptions<DataProtectionTokenProviderOptions>)_factory.Services.GetRequiredService(
            typeof(IOptions<>).MakeGenericType(optionsType));

        Assert.Equal(TimeSpan.FromMinutes(30), options.Value.TokenLifespan);
        Assert.Equal(AccountTokenProviders.PasswordReset, options.Value.Name);
    }

    /// <summary>
    /// O provider do Identity lê o relógio do sistema (não o <see cref="TimeProvider"/> da aplicação), então o
    /// teste não consegue avançar o tempo. Em vez disso recua o instante de emissão gravado no token real: os 8
    /// primeiros bytes do payload protegido são os ticks UTC da emissão. O caso de 29 minutos prova que o token
    /// regravado continua no formato aceito; só assim o de 31 minutos prova a validade e não um token malformado.
    /// </summary>
    private string AgeToken(string linkToken, TimeSpan age)
    {
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(AccountTokenProviders.PasswordReset);
        var identityToken = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(linkToken));
        var payload = protector.Unprotect(Convert.FromBase64String(identityToken));
        BinaryPrimitives.WriteInt64LittleEndian(payload, (DateTimeOffset.UtcNow - age).UtcTicks);
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Convert.ToBase64String(protector.Protect(payload))));
    }

    private async Task<AccountLink> RequestResetLinkAsync(Browser browser, string email)
    {
        await browser.FetchCsrfTokenAsync();
        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        return await Scenario.ReadLatestLinkAsync(email, "PasswordReset");
    }
}
