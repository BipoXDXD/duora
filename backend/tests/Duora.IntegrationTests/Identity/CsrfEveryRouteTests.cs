using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-13 enumerado: toda rota registrada que não é GET, chamada sem token antiforgery por um navegador com sessão
/// válida e corpo legítimo, responde 403 <c>CSRF_VALIDATION_FAILED</c> e não muda o banco. Rota nova sem corpo de
/// exemplo aqui quebra o teste, em vez de escapar da verificação.
/// </summary>
[Trait("Acceptance", "T-13")]
public sealed class CsrfEveryRouteTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task EveryMutatingRouteRejectsAMissingTokenWithoutSideEffects()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        var pendingEmail = IdentityScenario.NewEmail();
        var newEmail = IdentityScenario.NewEmail();
        using (var registrar = new Browser(_factory))
        using (await IdentityScenario.RegisterAsync(registrar, pendingEmail))
        {
        }

        var pendingLink = await scenario.ReadLatestLinkAsync(pendingEmail, "EmailConfirmation");
        using var browser = await scenario.SignedInBrowserAsync(account);
        var sessionId = (await SessionTests.ListSessionsAsync(browser)).Single().Id;
        var bodies = new Dictionary<string, object?>
        {
            ["POST /api/v1/auth/register"] = IdentityScenario.RegistrationBody(newEmail),
            ["POST /api/v1/auth/confirm-email"] = new
            {
                userId = pendingLink.UserId,
                token = pendingLink.Token,
                password = IdentityScenario.ValidPassword,
            },
            ["POST /api/v1/auth/login"] = new { email = account.Email, password = account.Password },
            ["POST /api/v1/auth/logout"] = null,
            ["POST /api/v1/auth/password-reset-requests"] = new { email = account.Email },
            ["POST /api/v1/auth/password-resets"] = new
            {
                userId = account.UserId,
                token = "token-qualquer",
                newPassword = "uma frase nova e longa",
            },
            ["DELETE /api/v1/me/sessions/{sessionId:guid}"] = null,
            ["POST /api/v1/me/age-verifications"] = new { },
        };
        string[] emails = [account.Email, pendingEmail, newEmail];
        var before = await SnapshotAsync(emails);

        var routes = MutatingRoutes();
        foreach (var (method, pattern) in routes)
        {
            var name = $"{method} {pattern}";
            Assert.True(bodies.TryGetValue(name, out var body), $"Sem corpo de exemplo para {name}: acrescente-o aqui.");
            var path = pattern.Replace("{sessionId:guid}", sessionId.ToString(), StringComparison.Ordinal);
            var headers = new MutationHeaders(SendCsrfToken: false);
            using var response = method == "DELETE"
                ? await browser.DeleteAsync(path, headers)
                : await browser.PostJsonAsync(path, body, headers);

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} respondeu {(int)response.StatusCode}.");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        }

        Assert.Equal(bodies.Keys.Order(StringComparer.Ordinal), routes.Select(r => $"{r.Method} {r.Pattern}").Order(StringComparer.Ordinal));
        Assert.Equal(before, await SnapshotAsync(emails));
    }

    private List<(string Method, string Pattern)> MutatingRoutes() =>
    [
        .. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (Method: method, Pattern: "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/'))))
            .Where(route => route.Method is not ("GET" or "HEAD" or "OPTIONS")),
    ];

    /// <summary>Estado das contas do teste: existência, confirmação, senha e stamp, e-mails, sessões ativas e verificações etárias.</summary>
    private async Task<string> SnapshotAsync(string[] emails)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select concat_ws('|',
                (select count(*) from identity.users where email = any($1)),
                (select count(*) from identity.users where email = any($1) and email_confirmed),
                (select string_agg(coalesce(password_hash, '') || security_stamp, ',' order by id)
                   from identity.users where email = any($1)),
                (select count(*) from ops.outbox_events where payload->>'to' = any($1)),
                (select count(*) from identity.login_sessions s join identity.users u on u.id = s.user_id
                   where u.email = any($1) and s.revoked_at is null),
                (select count(*) from identity.age_verifications v join identity.users u on u.id = v.user_id
                   where u.email = any($1)))
            """,
            connection);
        command.Parameters.AddWithValue(emails);
        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? "";
    }
}
