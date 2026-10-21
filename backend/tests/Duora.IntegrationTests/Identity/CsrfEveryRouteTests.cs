using System.Net;
using System.Text.Json;
using Duora.Core.Identity;
using Duora.IntegrationTests.Participation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-13 enumerado: toda rota registrada que não é GET, chamada sem token antiforgery por um navegador com sessão
/// válida e corpo legítimo, responde 403 <c>CSRF_VALIDATION_FAILED</c> e não muda o banco. Rota nova sem corpo de
/// exemplo aqui quebra o teste, em vez de escapar da verificação.
/// </summary>
[Trait("Acceptance", "T-13")]
public sealed class CsrfEveryRouteTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly DuoraWebFactory _factory;

    public CsrfEveryRouteTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new(postgres.ConnectionString) { Time = _time };
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task EveryMutatingRouteRejectsAMissingTokenWithoutSideEffects()
    {
        var scenario = new IdentityScenario(_postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        var pendingEmail = IdentityScenario.NewEmail();
        var newEmail = IdentityScenario.NewEmail();
        using (var registrar = new Browser(_factory))
        using (await IdentityScenario.RegisterAsync(registrar, pendingEmail))
        {
        }

        var pendingLink = await scenario.ReadLatestLinkAsync(pendingEmail, "EmailConfirmation");
        await MakeEligibleAsync(account.UserId);
        var administrator = await scenario.CreatePrivilegedAccountAsync(AccountRoles.ProductAdmin);
        await MakeEligibleAsync(administrator.UserId);
        using var adminBrowser = await scenario.SignedInBrowserAsync(administrator);
        await MfaScenario.EnableAsync(adminBrowser, _time);
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
            ["PATCH /api/v1/me/profile"] = new { displayName = "Sem token" },
            ["PUT /api/v1/me/blocks/{targetUserId:guid}"] = null,
            ["POST /api/v1/auth/reauthenticate"] = new { password = account.Password },
            ["POST /api/v1/me/mfa/enrollments"] = null,
            ["POST /api/v1/me/mfa/confirmations"] = new { code = "123456" },
            ["POST /api/v1/me/mfa/recovery-codes"] = null,
            ["DELETE /api/v1/me/mfa"] = null,
            ["POST /api/v1/me/password-changes"] = new
            {
                currentPassword = account.Password,
                newPassword = "uma frase nova e longa",
            },
            // O corpo de verdade é a imagem crua; sem token, a recusa vem antes de olhar o corpo.
            ["POST /api/v1/me/photos"] = null,
            ["POST /api/v1/me/age-verifications"] = new { },
            ["POST /api/v1/me/selfie-verifications"] = new { },
            ["POST /api/v1/me/selfie-verifications/{id:guid}/capture"] = new { },
            ["POST /api/v1/me/selfie-verifications/{id:guid}/complete"] = new { },
            ["PUT /api/v1/me/preferences"] = new { communicationLanguages = new[] { "en" }, allowDifferentDisplayLocales = false },
            ["PUT /api/v1/me/locale"] = new { locale = "en-US" },
            ["PUT /api/v1/events/{eventId:guid}/registration"] = null,
            ["POST /api/v1/events/{eventId:guid}/check-in"] = null,
            ["POST /api/v1/rooms/{roomId:guid}/accept"] = null,
            ["POST /api/v1/rooms/{roomId:guid}/reject"] = null,
            ["POST /api/v1/rooms/{roomId:guid}/exit"] = null,
            ["POST /api/v1/rooms/{roomId:guid}/actions"] = new { actionId = Guid.NewGuid(), expectedVersion = "1", type = "skip-card", cardId = "morning" },
            ["POST /api/v1/rooms/{roomId:guid}/routes-actions"] = new { actionId = Guid.NewGuid(), expectedVersion = "1", type = "acknowledge", stage = "Introduction" },
            ["POST /api/v1/admin/route-experience-versions"] = RoutesCatalogPublicationTests.Request(),
            ["POST /api/v1/admin/events"] = new { title = "Synthetic", startsAt = DateTimeOffset.UtcNow.AddDays(1), capacity = 10, experienceVersionId = Guid.NewGuid() },
            ["POST /api/v1/admin/experience-versions"] = new
            {
                contentVersion = 1,
                enUS = new { title = "Choices", objective = "Choose together", cards = new[] { new { cardId = "card", prompt = "Choose", options = new[] { new { optionId = "a", label = "A" }, new { optionId = "b", label = "B" } } } } },
                ptBR = new { title = "Escolhas", objective = "Escolham juntos", cards = new[] { new { cardId = "card", prompt = "Escolha", options = new[] { new { optionId = "a", label = "A" }, new { optionId = "b", label = "B" } } } } },
                audio = Array.Empty<object>(),
                editorialApproved = true,
            },
        };
        string[] emails = [account.Email, administrator.Email, pendingEmail, newEmail];
        var before = await SnapshotAsync(emails);

        var routes = MutatingRoutes();
        foreach (var (method, pattern) in routes)
        {
            var name = $"{method} {pattern}";
            Assert.True(bodies.TryGetValue(name, out var body), $"Sem corpo de exemplo para {name}: acrescente-o aqui.");
            var path = pattern
                .Replace("{id:guid}", account.UserId.ToString(), StringComparison.Ordinal)
                .Replace("{sessionId:guid}", sessionId.ToString(), StringComparison.Ordinal)
                .Replace("{targetUserId:guid}", pendingLink.UserId.ToString(), StringComparison.Ordinal)
                .Replace("{eventId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
                .Replace("{roomId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
            var headers = new MutationHeaders(SendCsrfToken: false);
            var caller = pattern.StartsWith("/api/v1/admin/", StringComparison.Ordinal) ? adminBrowser : browser;
            using var response = method switch
            {
                "DELETE" => await caller.DeleteAsync(path, headers),
                "PATCH" => await caller.PatchRawJsonAsync(path, JsonSerializer.Serialize(body, JsonSerializerOptions.Web), "\"0\"", headers),
                "PUT" => await caller.PutAsync(path, headers: headers),
                _ => await caller.PostJsonAsync(path, body, headers),
            };

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} respondeu {(int)response.StatusCode}.");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        }

        Assert.Equal(bodies.Keys.Order(StringComparer.Ordinal), routes.Select(r => $"{r.Method} {r.Pattern}").Order(StringComparer.Ordinal));
        Assert.Equal(before, await SnapshotAsync(emails));
    }

    private async Task MakeEligibleAsync(Guid user)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            with activated as (update identity.users set status='Active' where id=$1 returning id)
            insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,expires_at,completed_at,version)
            select $2,id,'Passed','test',$3,$3,$3,1 from activated
            """, connection);
        command.Parameters.AddWithValue(user);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(_time.GetUtcNow());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Rotas que dispensam CSRF por não serem de navegador; a lista é fechada (ADR-0024).</summary>
    private static readonly string[] CsrfExemptRoutes = ["POST /webhooks/{provider}"];

    [Fact]
    public void OnlyProviderWebhooksAreExemptFromCsrf()
    {
        var exempt = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<Duora.Web.Security.CsrfExemptEndpoint>() is not null)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => $"{method} /{(endpoint.RoutePattern.RawText ?? "").Trim('/')}"));

        Assert.Equal(CsrfExemptRoutes, exempt.Order(StringComparer.Ordinal));
    }

    private List<(string Method, string Pattern)> MutatingRoutes() =>
    [
        .. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (Method: method, Pattern: "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/'))))
            .Where(route => route.Method is not ("GET" or "HEAD" or "OPTIONS"))
            .Where(route => !CsrfExemptRoutes.Contains($"{route.Method} {route.Pattern}")),
    ];

    /// <summary>Estado das contas do teste: existência, confirmação, senha e stamp, e-mails, sessões ativas, perfis, bloqueios e verificações etárias.</summary>
    private async Task<string> SnapshotAsync(string[] emails)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
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
                (select count(*) from profiles.profiles p join identity.users u on u.id = p.user_id
                   where u.email = any($1)),
                (select count(*) from safety.blocks b join identity.users u on u.id = b.blocker_id
                   where u.email = any($1)),
                (select count(*) from identity.user_tokens t join identity.users u on u.id = t.user_id
                   where u.email = any($1)),
                (select count(*) from ops.audit_events a join identity.users u on u.id = a.target_id
                   where u.email = any($1)),
                (select count(*) from media.uploads m join identity.users u on u.id = m.owner_id
                   where u.email = any($1)),
                (select count(*) from identity.age_verifications v join identity.users u on u.id = v.user_id
                   where u.email = any($1)),
                (select count(*) from experiences.experience_versions v join identity.users u on u.id = v.approved_by
                   where u.email = any($1)))
            """,
            connection);
        command.Parameters.AddWithValue(emails);
        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? "";
    }
}
