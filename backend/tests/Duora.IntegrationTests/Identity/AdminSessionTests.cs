using System.Net;
using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Sessão administrativa (spec 4.3; auth.md): conta privilegiada abre sessão de 15 min ociosa e 8 h absoluta,
/// decidida no login pelos papéis. Participante continua com a sessão normal.
/// </summary>
public sealed class AdminSessionTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public AdminSessionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(AccountRoles.Moderator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task PrivilegedLoginOpensAnAdministrativeSessionWithoutMfaProof(string role)
    {
        var account = await Scenario.CreatePrivilegedAccountAsync(role);

        using var browser = await Scenario.SignedInBrowserAsync(account);

        var session = Assert.Single(await Scenario.SessionRowsAsync(account.UserId));
        Assert.Equal("Administrative", session.Policy);
        Assert.Null(session.MfaVerifiedAt);
        Assert.Equal(_time.GetUtcNow(), session.ReauthenticatedAt);
    }

    [Fact]
    public async Task ParticipantLoginOpensANormalSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.GrantRoleAsync(account.UserId, AccountRoles.Participant);

        using var browser = await Scenario.SignedInBrowserAsync(account);

        Assert.Equal("Normal", Assert.Single(await Scenario.SessionRowsAsync(account.UserId)).Policy);
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task AdministrativeSessionExpiresAfterFifteenIdleMinutes()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        _time.Advance(TimeSpan.FromMinutes(14));
        using var stillActive = await browser.GetAsync("/api/v1/me");
        _time.Advance(TimeSpan.FromMinutes(15));
        using var expired = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        await ProblemAssert.IsProblemAsync(expired, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task ActivityNeverKeepsAnAdministrativeSessionPastEightHours()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var step = TimeSpan.FromMinutes(10);
        var elapsed = TimeSpan.Zero;

        while (elapsed + step < TimeSpan.FromHours(8))
        {
            _time.Advance(step);
            elapsed += step;
            using var active = await browser.GetAsync("/api/v1/me");
            Assert.True(active.StatusCode == HttpStatusCode.OK, $"Sessão caiu com {elapsed} de uso contínuo.");
        }

        _time.Advance(step);
        using var pastAbsolute = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(pastAbsolute, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }
}
