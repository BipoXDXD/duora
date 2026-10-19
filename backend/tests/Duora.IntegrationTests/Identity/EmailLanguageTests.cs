using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// O idioma do e-mail vem do <c>Accept-Language</c> da requisição que o pede (cadastro e recuperação) e é gravado
/// no pedido da outbox. Só pt-BR e en-US passam (qualquer pt-* vira pt-BR, qualquer en-* vira en-US); o resto cai em en-US
/// (ADR-0026), sem erro para quem chamou.
/// </summary>
public sealed class EmailLanguageTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("en-US,en;q=0.9,pt-BR;q=0.8", "en-US")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("fr-FR,fr;q=0.9", "en-US")]
    [InlineData("fr-FR,en;q=0.5", "en-US")]
    [InlineData("pt-BR;q=0.2,en;q=0.8", "en-US")]
    [InlineData("en;q=0", "en-US")]
    [InlineData("*", "en-US")]
    [InlineData(";;;=,=", "en-US")]
    [InlineData("en;q=2", "en-US")]
    [InlineData("en-US;q=1.5", "en-US")]
    [InlineData("pt-BR;q=7,en;q=0.1", "en-US")]
    [InlineData("en\u0001US", "en-US")]
    [InlineData(null, "en-US")]
    public async Task RegistrationRecordsTheNegotiatedLanguage(string? acceptLanguage, string expected)
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/register",
            IdentityScenario.RegistrationBody(email),
            WithAcceptLanguage(acceptLanguage));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(expected, await Scenario.ReadLatestLanguageAsync(email, "EmailConfirmation"));
    }

    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("es", "en-US")]
    [InlineData(null, "en-US")]
    public async Task PasswordResetRequestRecordsTheNegotiatedLanguage(string? acceptLanguage, string expected)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/password-reset-requests",
            new { email = account.Email },
            WithAcceptLanguage(acceptLanguage));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(expected, await Scenario.ReadLatestLanguageAsync(account.Email, "PasswordReset"));
    }

    [Fact]
    public async Task ResetRequestForUnknownAddressAlsoFollowsTheRequestLanguage()
    {
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/password-reset-requests",
            new { email },
            WithAcceptLanguage("en"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("en-US", await Scenario.ReadLatestLanguageAsync(email, "PasswordResetForUnknownAddress"));
    }

    private static MutationHeaders WithAcceptLanguage(string? value) =>
        value is null
            ? MutationHeaders.SameOrigin
            : new MutationHeaders(Extra: new Dictionary<string, string> { ["Accept-Language"] = value });
}
