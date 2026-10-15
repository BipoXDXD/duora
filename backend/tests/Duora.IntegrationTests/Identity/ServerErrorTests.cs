using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Erro inesperado (aqui, banco que recusa conexão no meio do login) vira <c>500 INTERNAL_ERROR</c> em Problem
/// Details sem nada de dentro: exceção, pilha, driver, SQL, namespace nem a senha da connection string.
/// </summary>
public sealed class ServerErrorTests
{
    [Fact]
    public async Task UnexpectedFailureIsAGenericProblemWithoutInternals()
    {
        var connection = new CanaryConnectionString();
        await using var factory = new DuoraWebFactory(connection.Value);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = IdentityScenario.NewEmail(), password = "qualquer senha longa" });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string[] internals = ["Exception", "at ", "Npgsql", "select", "SELECT", "Duora.", "Microsoft.", connection.Password];
        Assert.DoesNotContain(internals, body.Contains);
    }
}
