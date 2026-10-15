using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Pré-sequestro de conta (TM-66): o cadastro de quem tem o e-mail substitui um cadastro pendente feito por outra
/// pessoa; cadastro pendente expira; reset de senha não serve a cadastro pendente.
/// </summary>
[Trait("Threat", "TM-66")]
public sealed class AccountTakeoverTests(PostgresFixture postgres)
{
    private const string AttackerPassword = "senha escolhida pelo atacante";
    private const string OwnerPassword = "senha escolhida pela titular";

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
        using var attackerConfirmation = await attacker.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = links[0].UserId, token = links[0].Token });
        using var ownerConfirmation = await owner.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = links[1].UserId, token = links[1].Token });
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
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var link = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");

        time.Advance(TimeSpan.FromHours(2));
        using var confirmation = await browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token });

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
}
