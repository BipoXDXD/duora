using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Endereços que colidem na normalização (TM-67): "ſam@" vira "SAM@" em maiúsculas e acharia a conta de "sam@".
/// Nenhum caminho pode alcançar a conta de outra pessoa nem mandar o link para o endereço digitado.
/// </summary>
[Trait("Threat", "TM-67")]
public sealed class LookalikeEmailTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task PasswordResetForALookalikeAddressNeverReachesTheAccount()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var lookalike = "ſ" + account.Email[1..];
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = lookalike });

        await ProblemAssert.IsProblemAsync(request, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(lookalike));
        Assert.Empty(await Scenario.ReadLinksAsync(account.Email, "PasswordReset"));
    }

    [Fact]
    public async Task RegistrationWithALookalikeAddressLeavesTheStoredEmailUntouched()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var lookalike = "ſ" + account.Email[1..];
        using var browser = new Browser(_factory);

        using var registration = await IdentityScenario.RegisterAsync(browser, lookalike);

        await ProblemAssert.IsProblemAsync(registration, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(1, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and email = $2",
            account.UserId,
            account.Email));
    }

    [Fact]
    public async Task ResetLinkGoesToTheStoredAddressNotToTheTypedVariant()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var typed = account.Email.ToUpperInvariant();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var request = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = typed });

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Single(await Scenario.ReadLinksAsync(account.Email, "PasswordReset"));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(typed));
    }

    [Fact]
    public async Task ExistingAccountNoticeGoesToTheStoredAddressNotToTheTypedVariant()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var typed = account.Email.ToUpperInvariant();
        using var browser = new Browser(_factory);

        using var registration = await IdentityScenario.RegisterAsync(browser, typed);

        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        Assert.Single(await Scenario.ReadLinksAsync(account.Email, "RegistrationAttemptOnExistingAccount"));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(typed));
    }
}
