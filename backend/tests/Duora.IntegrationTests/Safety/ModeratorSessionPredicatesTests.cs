using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// A autorização viva do moderador (ADR-0042) condição por condição: cada predicado é violado sozinho, o
/// serviço recusa, e ao restaurar exatamente o limite (on point) volta a aceitar. Parâmetros dos SQLs: <c>$1</c>
/// a conta, <c>$2</c> a sessão administrativa e <c>$3</c> o instante do relógio falso.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class ModeratorSessionPredicatesTests : IAsyncDisposable
{
    private const string ModeratorRole = AccountRoles.Moderator;

    private const long TicksPerMicrosecond = 10;

    /// <summary>Predicado → (SQL que o viola sozinho, SQL que o devolve ao limite aceito).</summary>
    private static readonly Dictionary<string, (string Violate, string Restore)> Predicates =
        new(StringComparer.Ordinal)
        {
            ["account-not-active"] = (
                "update identity.users set status = 'Restricted' where id = $1",
                "update identity.users set status = 'Active' where id = $1"),
            ["email-not-confirmed"] = (
                "update identity.users set email_confirmed = false where id = $1",
                "update identity.users set email_confirmed = true where id = $1"),
            ["two-factor-off"] = (
                "update identity.users set two_factor_enabled = false where id = $1",
                "update identity.users set two_factor_enabled = true where id = $1"),
            ["temporary-password-pending"] = (
                "update identity.users set temporary_password_expires_at = $3 + interval '1 day' where id = $1",
                "update identity.users set temporary_password_expires_at = null where id = $1"),
            ["role-revoked"] = (
                $"""
                delete from identity.user_roles
                where user_id = $1 and role_id = (select id from identity.roles where name = '{ModeratorRole}')
                """,
                $"""
                insert into identity.user_roles (user_id, role_id)
                select $1, id from identity.roles where name = '{ModeratorRole}'
                """),
            ["session-not-administrative"] = (
                "update identity.login_sessions set policy = 'Normal' where id = $2",
                "update identity.login_sessions set policy = 'Administrative' where id = $2"),
            ["security-stamp-changed"] = (
                "update identity.users set security_stamp = 'rotated-' || security_stamp where id = $1",
                """
                update identity.users u set security_stamp = s.security_stamp
                from identity.login_sessions s where u.id = $1 and s.id = $2
                """),
            ["session-revoked"] = (
                "update identity.login_sessions set revoked_at = $3 where id = $2",
                "update identity.login_sessions set revoked_at = null where id = $2"),
            ["idle-window-ended-exactly-now"] = (
                "update identity.login_sessions set idle_expires_at = $3 where id = $2",
                "update identity.login_sessions set idle_expires_at = $3 + interval '1 microsecond' where id = $2"),
            ["second-factor-never-verified"] = (
                "update identity.login_sessions set mfa_verified_at = null where id = $2",
                "update identity.login_sessions set mfa_verified_at = $3 - interval '1 minute' where id = $2"),
            ["second-factor-verified-in-the-future"] = (
                "update identity.login_sessions set mfa_verified_at = $3 + interval '1 microsecond' where id = $2",
                "update identity.login_sessions set mfa_verified_at = $3 where id = $2"),
        };

    private readonly FakeTimeProvider _time =
        new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public ModeratorSessionPredicatesTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> PredicateNames() => [.. Predicates.Keys];

    [Theory]
    [MemberData(nameof(PredicateNames))]
    public async Task EachPredicateOfTheLiveAuthorizationRefusesTheModeratorAloneAndAcceptsItAtTheLimit(
        string predicate)
    {
        var session = await ModeratorSessionAsync();
        var (violate, restore) = Predicates[predicate];
        Assert.True(await IsModeratorAsync(session));

        await _scenario.ExecuteAsync(violate, session.UserId, session.SessionId, _time.GetUtcNow());
        Assert.False(await IsModeratorAsync(session));

        await _scenario.ExecuteAsync(restore, session.UserId, session.SessionId, _time.GetUtcNow());
        Assert.True(await IsModeratorAsync(session));
    }

    [Fact]
    public async Task ASessionIdThatBelongsToAnotherAccountDoesNotAuthorizeTheModerator()
    {
        var mine = await ModeratorSessionAsync();
        var other = await ModeratorSessionAsync();

        Assert.False(await IsModeratorAsync(new ModeratorSession(mine.UserId, other.SessionId)));
        Assert.True(await IsModeratorAsync(mine));
    }

    [Fact]
    public async Task AnEmptyAccountOrSessionIdNeverAuthorizes()
    {
        var session = await ModeratorSessionAsync();

        Assert.False(await IsModeratorAsync(new ModeratorSession(Guid.Empty, session.SessionId)));
        Assert.False(await IsModeratorAsync(new ModeratorSession(session.UserId, Guid.Empty)));
        Assert.True(await IsModeratorAsync(session));
    }

    /// <summary>
    /// O TOTP recente vale para os comandos e não para a pré-checagem da fronteira. Com um alvo que não existe, o
    /// comando só chega a <c>NotFound</c> se a autorização passou (os dois checks do ciclo), e recusado é
    /// <c>Forbidden</c>.
    /// </summary>
    [Theory]
    [InlineData(-3_600_000_000L, true)]
    [InlineData(-3_600_000_001L, false)]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    public async Task ARecentTotpIsRequiredByTheCommandsFromTheEndOfTheWindowAndNeverByThePrecheck(
        long microsecondsFromNow,
        bool recent)
    {
        Assert.Equal(TimeSpan.FromHours(1), RecentAuthentication.Window);
        var session = await ModeratorSessionAsync();
        var proof = _time.GetUtcNow() + TimeSpan.FromTicks(microsecondsFromNow * TicksPerMicrosecond);
        await _scenario.ExecuteAsync(
            "update identity.login_sessions set totp_verified_at = $2 where id = $1",
            session.SessionId,
            proof);

        var outcome = await LiftAsync(session);

        Assert.True(await IsModeratorAsync(session));
        if (recent)
        {
            Assert.IsType<LiftOutcome.NotFound>(outcome);
        }
        else
        {
            Assert.IsType<LiftOutcome.Forbidden>(outcome);
        }
    }

    [Fact]
    public async Task ASessionWithoutAnyTotpProofPassesThePrecheckButCannotRunACommand()
    {
        var session = await ModeratorSessionAsync();
        await _scenario.ExecuteAsync(
            "update identity.login_sessions set totp_verified_at = null where id = $1",
            session.SessionId);

        Assert.True(await IsModeratorAsync(session));
        Assert.IsType<LiftOutcome.Forbidden>(await LiftAsync(session));
    }

    private async Task<ModeratorSession> ModeratorSessionAsync()
    {
        var (account, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        return new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(browser));
    }

    private async Task<bool> IsModeratorAsync(ModeratorSession session)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var suspensions = scope.ServiceProvider.GetRequiredService<AccountSuspensions>();
        return await suspensions.IsModeratorSessionAsync(session, Ct);
    }

    private async Task<LiftOutcome> LiftAsync(ModeratorSession session)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().LiftAsModeratorAsync(
            session, Guid.NewGuid(), Guid.NewGuid(), 1, SuspensionLift.ByModerator(session.UserId, "Motivo."), Ct);
    }
}
