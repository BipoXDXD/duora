using System.Net;
using System.Security.Claims;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Policy <c>EligibleParticipant</c> (T-01, parcial; INV-01; ADR-0024): só conta <c>Active</c> com verificação
/// adulta participa, e a verificação adulta não vence. Pendente, restrita ou suspensa, não.
/// </summary>
[Trait("Acceptance", "T-01")]
public sealed class EligibleParticipantPolicyTests : IAsyncDisposable
{
    /// <summary>
    /// Rotas do titular que ficam na política padrão de participante sem exigir elegibilidade: o próprio perfil e as
    /// fotos dele, o bloqueio (segurança) e a verificação etária, que a pessoa precisa justamente enquanto não é elegível (spec 8.2).
    /// As rotas públicas (<c>AllowAnonymous</c>) e as que declaram política de conta, de cadastro de MFA ou
    /// administrativa (<see cref="SessionAuthorization"/>) também não são de participação. Toda outra rota de
    /// <c>/api/v1</c> exige <see cref="ParticipationPolicies.EligibleParticipant"/>.
    /// </summary>
    private static readonly string[] TitularRoutes =
    [
        "GET /api/v1/me/age-verifications/current",
        "GET /api/v1/me/photos/{photoId:guid}/status",
        "GET /api/v1/me/profile",
        "PATCH /api/v1/me/profile",
        "POST /api/v1/me/age-verifications",
        "POST /api/v1/me/photos",
        "PUT /api/v1/me/blocks/{targetUserId:guid}",
    ];

    private static readonly string[] NonParticipationPolicies =
    [
        SessionAuthorization.AccountSession,
        SessionAuthorization.SignedIn,
        SessionAuthorization.AdministrativeSession,
    ];

    private const string SentinelRoute = "GET /api/v1/test/eligible-participant";
    private const string SentinelPath = "/api/v1/test/eligible-participant";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;

    public EligibleParticipantPolicyTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            Settings = AgeVerificationScenario.FakeProviderSettings(),
        };
    }

    private AgeVerificationScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void EveryParticipationRouteRequiresTheEligibleParticipantPolicy()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? "").StartsWith("/api/v1", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (Name: $"{method} /{(endpoint.RoutePattern.RawText ?? "").Trim('/')}", Endpoint: endpoint)))
            .ToList();

        var participation = routes
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(route => !route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => NonParticipationPolicies.Contains(data.Policy)))
            .ToList();
        var unknownTitularRoutes = TitularRoutes.Except(participation.Select(r => r.Name), StringComparer.Ordinal);
        var unprotected = participation
            .Where(route => !TitularRoutes.Contains(route.Name))
            .Where(route => !route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy == ParticipationPolicies.EligibleParticipant))
            .Select(route => route.Name);
        var anonymousParticipation = routes
            .Where(route => route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy == ParticipationPolicies.EligibleParticipant))
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(route => route.Name);

        Assert.Empty(unknownTitularRoutes);
        // A sentinela do ambiente de teste garante que a enumeração enxerga ao menos uma rota de participação.
        Assert.Contains(SentinelRoute, participation.Select(route => route.Name));
        Assert.Empty(unprotected);
        Assert.Empty(anonymousParticipation);
    }

    [Fact]
    public async Task ActiveAccountWithAnAdultVerificationIsEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());

        Assert.True(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task PendingEligibilityAccountIsNotEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task AdultVerificationStaysEligibleYearsLater()
    {
        // Verificação só no cadastro (decisão do dono, 2026-10-16): nada vence com o tempo.
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());

        var right = await AuthorizeAsync(account.UserId);
        _time.Advance(TimeSpan.FromDays(366));
        var yearLater = await AuthorizeAsync(account.UserId);
        _time.Advance(TimeSpan.FromDays(10 * 366));

        Assert.True(right);
        Assert.True(yearLater);
        Assert.True(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task AdultVerificationStillCountsWhenALaterRequestExists()
    {
        // Linha antiga (do tempo da validade de 365 dias): pedido aberto ou expirado depois da verificação adulta não
        // apaga a verificação.
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow().AddDays(-400));
        await Scenario.ExecuteAsync(
            """
            insert into identity.age_verifications (id, user_id, state, policy_version, provider, request_nonce, requested_at, version)
            values (gen_random_uuid(), $1, 'Expired', 'test-2026-10', 'fake', 'nonce-' || gen_random_uuid(), $2, 1)
            """,
            account.UserId,
            _time.GetUtcNow().AddDays(-30));

        Assert.True(await AuthorizeAsync(account.UserId));
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("ManualReview")]
    public async Task LaterDecisionOverridesAnOlderAdultVerification(string laterState)
    {
        // Falha fechada: uma decisão posterior à verificação adulta (menor ou inconclusiva, linha antiga ou conta
        // devolvida a Active por outro caminho) manda, mesmo com a conta ativa.
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow().AddDays(-400));
        await Scenario.AddDecidedVerificationAsync(account.UserId, laterState, _time.GetUtcNow().AddDays(-30));

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Theory]
    [InlineData("Rejected", "Expired")]
    [InlineData("Rejected", "Pending")]
    [InlineData("ManualReview", "Expired")]
    public async Task LaterDecisionStillCountsWhenAnUndecidedRequestFollowsIt(string decision, string request)
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow().AddDays(-400));
        await Scenario.AddDecidedVerificationAsync(account.UserId, decision, _time.GetUtcNow().AddDays(-100));
        await Scenario.AddUndecidedRequestAsync(account.UserId, request, _time.GetUtcNow().AddDays(-30));

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    [InlineData("DeletionPending")]
    public async Task AccountOutsideTheFlowIsNotEligibleEvenWithAnAdultVerification(string status)
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());
        await Scenario.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task PrincipalWithoutSessionClaimIsNotEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());

        Assert.False(await AuthorizeAsync(Principal(account.UserId, sessionId: null)));
    }

    [Fact]
    public async Task AnonymousIsNotEligible()
    {
        Assert.False(await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task EligibleAccountReachesAParticipationRoute()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);

        using var response = await browser.GetAsync(SentinelPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PendingAccountIsForbiddenOnAParticipationRoute()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);

        using var response = await browser.GetAsync(SentinelPath);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task RestrictedAccountIsForbiddenOnAParticipationRoute()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);
        await Scenario.ExecuteAsync("update identity.users set status = 'Restricted' where id = $1", account.UserId);

        using var response = await browser.GetAsync(SentinelPath);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task AdultVerifiedYearsAgoReachesAParticipationRoute()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow().AddDays(-10 * 366));
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);

        using var response = await browser.GetAsync(SentinelPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableDatabaseIsTemporarilyUnavailableNotForbidden()
    {
        // Banco fora do ar durante a consulta de elegibilidade: 503, para o cliente tentar de novo, nunca 403 (M7).
        var dead = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        await using var factory = new DuoraWebFactory(dead) { Settings = AgeVerificationScenario.FakeProviderSettings() };
        await using var scope = factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        var policy = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();

        var result = await authorization.AuthorizeAsync(Principal(Guid.CreateVersion7(), Guid.CreateVersion7()), ParticipationPolicies.EligibleParticipant);
        await scope.ServiceProvider.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ => Task.CompletedTask,
            context,
            (await policy.GetPolicyAsync(ParticipationPolicies.EligibleParticipant))!,
            Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult.Forbid(result.Failure));

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("TEMPORARILY_UNAVAILABLE", body, StringComparison.Ordinal);
    }

    /// <summary>Principal como o ticket de sessão o monta (<see cref="DuoraClaims"/>), com uma sessão qualquer.</summary>
    private Task<bool> AuthorizeAsync(Guid userId) => AuthorizeAsync(Principal(userId, Guid.CreateVersion7()));

    private static ClaimsPrincipal Principal(Guid userId, Guid? sessionId)
    {
        // Como o ticket de uma sessão normal de participante (DuoraClaims.ForSignIn), que a política padrão confere.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new(DuoraClaims.Policy, nameof(SessionPolicy.Normal)),
        };
        if (sessionId is { } id)
        {
            claims.Add(new Claim(DuoraClaims.SessionId, id.ToString("D")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, DuoraAuthentication.Scheme));
    }

    /// <summary>Avalia a policy real, com o banco real, num escopo de DI como o de uma requisição.</summary>
    private async Task<bool> AuthorizeAsync(ClaimsPrincipal principal)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(principal, ParticipationPolicies.EligibleParticipant)).Succeeded;
    }
}
