using System.Security.Claims;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Policy <c>EligibleParticipant</c> (T-01, parcial; INV-01; ADR-0024): só conta <c>Active</c> com verificação
/// adulta dentro da validade participa. Pendente, restrita, suspensa ou com verificação vencida, não.
/// </summary>
[Trait("Acceptance", "T-01")]
public sealed class EligibleParticipantPolicyTests : IAsyncDisposable
{
    /// <summary>
    /// Rotas do titular: conta, sessão, verificação, o próprio perfil e o bloqueio (segurança). A pessoa precisa delas
    /// justamente enquanto ainda não é elegível (spec 8.2: conta restrita acessa a própria conta, segurança e recurso).
    /// Toda outra rota de <c>/api/v1</c> é de participação e exige a policy; rota nova fora desta lista sem a policy
    /// quebra o teste.
    /// </summary>
    private static readonly string[] AccountRoutes =
    [
        "DELETE /api/v1/me/sessions/{sessionId:guid}",
        "GET /api/v1/auth/csrf",
        "GET /api/v1/me",
        "GET /api/v1/me/age-verifications/current",
        "GET /api/v1/me/profile",
        "GET /api/v1/me/sessions",
        "POST /api/v1/auth/confirm-email",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/password-reset-requests",
        "POST /api/v1/auth/password-resets",
        "POST /api/v1/auth/register",
        "PATCH /api/v1/me/profile",
        "POST /api/v1/me/age-verifications",
        "PUT /api/v1/me/blocks/{targetUserId:guid}",
    ];

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

        var unknownAccountRoutes = AccountRoutes.Except(routes.Select(r => r.Name), StringComparer.Ordinal);
        var unprotected = routes
            .Where(route => !AccountRoutes.Contains(route.Name))
            .Where(route => !route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy == ParticipationPolicies.EligibleParticipant))
            .Select(route => route.Name);

        Assert.Empty(unknownAccountRoutes);
        Assert.Empty(unprotected);
    }

    [Fact]
    public async Task ActiveAccountWithAValidVerificationIsEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow(), _time.GetUtcNow().AddDays(30));

        Assert.True(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task PendingEligibilityAccountIsNotEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task VerificationPastItsValidityIsNotEligibleAndNeverComesBackByItself()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow(), _time.GetUtcNow().AddDays(1));

        var before = await AuthorizeAsync(account.UserId);
        _time.Advance(TimeSpan.FromDays(1));
        var atExpiry = await AuthorizeAsync(account.UserId);
        _time.Advance(TimeSpan.FromDays(400));

        Assert.True(before);
        Assert.False(atExpiry);
        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    [InlineData("DeletionPending")]
    public async Task AccountOutsideTheFlowIsNotEligibleEvenWithAValidVerification(string status)
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow(), _time.GetUtcNow().AddDays(30));
        await Scenario.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);

        Assert.False(await AuthorizeAsync(account.UserId));
    }

    [Fact]
    public async Task PrincipalWithoutSessionClaimIsNotEligible()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow(), _time.GetUtcNow().AddDays(30));

        Assert.False(await AuthorizeAsync(Principal(account.UserId, sessionId: null)));
    }

    [Fact]
    public async Task AnonymousIsNotEligible()
    {
        Assert.False(await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>Principal como o ticket de sessão o monta (<see cref="DuoraClaims"/>), com uma sessão qualquer.</summary>
    private Task<bool> AuthorizeAsync(Guid userId) => AuthorizeAsync(Principal(userId, Guid.CreateVersion7()));

    private static ClaimsPrincipal Principal(Guid userId, Guid? sessionId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString("D")) };
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
