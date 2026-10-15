using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Pré-sequestro de conta (TM-66). Confirmar o e-mail exige o link (posse do e-mail) e a senha do cadastro pendente
/// (conhecimento da senha): um link gerado por outra pessoa é inofensivo, qualquer que seja a ordem dos cadastros.
/// </summary>
[Trait("Threat", "TM-66")]
public sealed class AccountTakeoverTests(PostgresFixture postgres)
{
    private const string AttackerPassword = "senha escolhida pelo atacante";
    private const string OwnerPassword = "senha escolhida pela titular";

    [Fact]
    public async Task OwnerCannotBeTrickedIntoConfirmingTheAttackersLaterRegistration()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var owner = new Browser(factory);
        using var attacker = new Browser(factory);

        using var ownerFirst = await IdentityScenario.RegisterAsync(owner, email, OwnerPassword);
        using var attackerSecond = await IdentityScenario.RegisterAsync(attacker, email, AttackerPassword);
        var attackersLink = (await scenario.ReadLinksAsync(email, "EmailConfirmation"))[^1];
        using var trickedConfirmation = await ConfirmAsync(owner, attackersLink, OwnerPassword);
        using var ownerAgain = await IdentityScenario.RegisterAsync(owner, email, OwnerPassword);
        var ownersLink = (await scenario.ReadLinksAsync(email, "EmailConfirmation"))[^1];
        using var ownerConfirmation = await ConfirmAsync(owner, ownersLink, OwnerPassword);
        using var attackerLogin = await IdentityScenario.LoginAsync(attacker, email, AttackerPassword);
        using var ownerLogin = await IdentityScenario.LoginAsync(owner, email, OwnerPassword);

        await ProblemAssert.IsProblemAsync(trickedConfirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, ownerConfirmation.StatusCode);
        await ProblemAssert.IsProblemAsync(attackerLogin, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, ownerLogin.StatusCode);
        Assert.Equal(1, await scenario.CountUsersAsync(email));
    }

    [Fact]
    public async Task OwnersRegistrationReplacesAPendingRegistrationMadeBySomeoneElse()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var attacker = new Browser(factory);
        using var owner = new Browser(factory);

        using var attackerRegistration = await IdentityScenario.RegisterAsync(attacker, email, AttackerPassword);
        using var ownerRegistration = await IdentityScenario.RegisterAsync(owner, email, OwnerPassword);
        var links = await scenario.ReadLinksAsync(email, "EmailConfirmation");
        using var attackerConfirmation = await ConfirmAsync(attacker, links[0], AttackerPassword);
        using var ownerConfirmation = await ConfirmAsync(owner, links[1], OwnerPassword);
        using var attackerLogin = await IdentityScenario.LoginAsync(attacker, email, AttackerPassword);
        using var ownerLogin = await IdentityScenario.LoginAsync(owner, email, OwnerPassword);

        Assert.Equal(2, links.Count);
        Assert.Equal(1, await scenario.CountUsersAsync(email));
        await ProblemAssert.IsProblemAsync(attackerConfirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, ownerConfirmation.StatusCode);
        await ProblemAssert.IsProblemAsync(attackerLogin, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, ownerLogin.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordAndInvalidTokenAreIndistinguishable()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        using var wrongPassword = await ConfirmAsync(browser, link, AttackerPassword);
        using var wrongToken = await ConfirmAsync(browser, link with { Token = "nao-e-um-token" }, OwnerPassword);

        await ProblemAssert.IsProblemAsync(wrongPassword, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        await ProblemAssert.IsProblemAsync(wrongToken, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongPassword),
            await ProblemAssert.BodyWithoutTraceIdAsync(wrongToken));
    }

    [Fact]
    public async Task TooManyWrongPasswordsInvalidateTheConfirmationLink()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await ConfirmAsync(browser, link, $"tentativa errada numero {attempt}");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var rightPasswordTooLate = await ConfirmAsync(browser, link, OwnerPassword);

        await ProblemAssert.IsProblemAsync(rightPasswordTooLate, HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    [Fact]
    public async Task ConfirmedAccountIsNotReplacedByALaterRegistration()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync(OwnerPassword);
        using var attacker = new Browser(factory);
        using var owner = new Browser(factory);

        using var registration = await IdentityScenario.RegisterAsync(attacker, account.Email, AttackerPassword);
        using var attackerLogin = await IdentityScenario.LoginAsync(attacker, account.Email, AttackerPassword);
        using var ownerLogin = await IdentityScenario.LoginAsync(owner, account.Email, OwnerPassword);

        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        await ProblemAssert.IsProblemAsync(attackerLogin, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, ownerLogin.StatusCode);
    }

    [Fact]
    public async Task PendingRegistrationExpiresAndCanNoLongerBeConfirmed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = time,
            // Prazo curto, abaixo da validade de 24 h do token: quem barra é o prazo da conta pendente.
            Settings = new Dictionary<string, string?> { ["Identity:PendingAccountLifetime"] = "01:00:00" },
        };
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, OwnerPassword);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        time.Advance(TimeSpan.FromHours(2));
        using var confirmation = await ConfirmAsync(browser, link, OwnerPassword);

        await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    [Fact]
    public async Task PasswordResetIsNotOfferedForAPendingRegistration()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email, AttackerPassword);

        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email });

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Empty(await scenario.ReadLinksAsync(email, "PasswordReset"));
        Assert.Single(await scenario.ReadLinksAsync(email, "PendingRegistrationReminder"));
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Browser browser, AccountLink link, string password) =>
        browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token, password });
}
