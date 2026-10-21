using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// TM-21: o aviso ao titular e a mudança de credencial confirmam juntos. Se a transação falha depois de o aviso já ter
/// sido gravado, nada fica: nem aviso na outbox (o titular não é avisado de uma mudança que não houve), nem a mudança,
/// nem a revogação das sessões, nem o registro de auditoria.
/// </summary>
public sealed class CredentialChangeRollbackTests : IAsyncDisposable
{
    private const string PasswordChanges = "/api/v1/me/password-changes";
    private const string PasswordResets = "/api/v1/auth/password-resets";
    private const string NewPassword = "uma frase nova e bem longa";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly FailAfterNotice _failure = new();
    private readonly DuoraWebFactory _factory;

    public CredentialChangeRollbackTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            TestServices = services => services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(_failure)),
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AFailedRecoveryNoticeRollsBackConsumptionAndAllowsRetry()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var enrolled = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(enrolled, _time);
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);
        using var browser = new Browser(_factory);
        using var failed = await MfaScenario.LoginAsync(browser, account, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        using var retry = await MfaScenario.LoginAsync(browser, account, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task AFailedPasswordChangeLeavesNoNoticeAndNoChange()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        await AssertOldPasswordStillSignsInAsync(account);
    }

    [Fact]
    public async Task AFailedPasswordResetLeavesNoNoticeAndNoChange()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var signedIn = await Scenario.SignedInBrowserAsync(account);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        using var requested = await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = account.Email });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var link = await Scenario.ReadLatestLinkAsync(account.Email, "PasswordReset");
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);

        using var response = await browser.PostJsonAsync(
            PasswordResets,
            new { userId = link.UserId, token = link.Token, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        await AssertOldPasswordStillSignsInAsync(account);
    }

    [Fact]
    public async Task AFailedMfaActivationLeavesNoNoticeAndMfaOff()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);

        using var response = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Next(_time, sharedKey) });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        Assert.Equal(0, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and two_factor_enabled",
            account.UserId));
    }

    [Fact]
    public async Task AFailedMfaDeactivationLeavesNoNoticeAndMfaOn()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);

        using var response = await browser.DeleteAsync(MfaScenario.Mfa);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        Assert.Equal(1, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and two_factor_enabled",
            account.UserId));
    }

    [Fact]
    public async Task AFailedRecoveryCodeRegenerationLeavesNoNoticeAndOldCodesValid()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser, _time);
        var before = await AccountStateAsync(account.UserId);
        _failure.Arm(account.UserId);

        using var response = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(account.UserId));
        using var login = await MfaScenario.LoginAsync(new Browser(_factory), account, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task AFailedTemporaryPasswordReissueLeavesNoNoticeAndNoChange()
    {
        var email = IdentityScenario.NewEmail();
        var grant = await RunAdminAsync("admin", "grant", "--email", email, "--role", "moderator", "--operator", "ana.ops");
        Assert.Equal(0, grant.ExitCode);
        var temporaryPassword = grant.Output.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith(Duora.Web.Admin.AdminCommand.PasswordLinePrefix, StringComparison.Ordinal))
            [Duora.Web.Admin.AdminCommand.PasswordLinePrefix.Length..];
        var userId = Guid.Parse(await Scenario.ScalarTextAsync(
            "select id::text from identity.users where normalized_email = upper($1)", email));
        using var signedIn = new Browser(_factory);
        using (var login = await IdentityScenario.LoginAsync(signedIn, email, temporaryPassword))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        var before = await AccountStateAsync(userId);
        var expiresBefore = await Scenario.ScalarTextAsync(
            "select temporary_password_expires_at::text from identity.users where id = $1", userId);
        _failure.Arm(userId);

        await Assert.ThrowsAnyAsync<Exception>(() => RunAdminAsync(
            "admin", "grant", "--email", email, "--role", "moderator", "--operator", "ana.ops", "--reissue"));

        Assert.True(_failure.Fired);
        Assert.Equal(before, await AccountStateAsync(userId));
        Assert.Equal(expiresBefore, await Scenario.ScalarTextAsync(
            "select temporary_password_expires_at::text from identity.users where id = $1", userId));
        using var again = new Browser(_factory);
        using var withOld = await IdentityScenario.LoginAsync(again, email, temporaryPassword);
        Assert.Equal(HttpStatusCode.NoContent, withOld.StatusCode);
    }

    private async Task<(int ExitCode, string Output)> RunAdminAsync(params string[] args)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var exitCode = await Duora.Web.Admin.AdminCommand.ExecuteAsync(
            _factory.Services,
            args,
            output,
            error,
            TestContext.Current.CancellationToken);
        return (exitCode, output.ToString());
    }

    private async Task AssertOldPasswordStillSignsInAsync(TestAccount account)
    {
        using var browser = new Browser(_factory);
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    /// <summary>
    /// O que a mudança tocaria: credencial e stamp da conta, sessões revogadas, auditoria e avisos. Iguais antes e
    /// depois da falha provam que a transação inteira voltou.
    /// </summary>
    private async Task<string> AccountStateAsync(Guid userId) =>
        await Scenario.ScalarTextAsync(
            """
            select concat_ws('|',
                u.password_hash, u.security_stamp, u.two_factor_enabled, u.access_failed_count,
                (select count(*) from identity.login_sessions s where s.user_id = u.id and s.revoked_at is not null),
                (select count(*) from ops.audit_events a where a.target_id = u.id),
                (select count(*) from ops.outbox_events e
                 where e.type = 'identity.account-email-requested'
                   and e.payload->>'userId' = u.id::text and e.payload->>'kind' like '%Notice'))
            from identity.users u where u.id = $1
            """,
            userId);

    /// <summary>
    /// Falha forçada logo depois de o aviso ao titular ser gravado (o INSERT já rodou na transação), antes do commit.
    /// Só dispara uma vez e só para a conta armada, para não tocar o que o teste faz antes.
    /// </summary>
    private sealed class FailAfterNotice : SaveChangesInterceptor
    {
        private Guid? _userId;
        private bool _noticePending;

        public bool Fired { get; private set; }

        public void Arm(Guid userId) => _userId = userId;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _noticePending = _userId is { } userId && !Fired && eventData.Context is { } context
                && context.ChangeTracker.Entries<OutboxEvent>().Any(entry =>
                    entry.State == EntityState.Added && IsNoticeFor(entry.Entity, userId));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (!_noticePending)
            {
                return ValueTask.FromResult(result);
            }

            _noticePending = false;
            Fired = true;
            throw new InvalidOperationException("Falha forçada pelo teste depois de gravar o aviso.");
        }

        private static bool IsNoticeFor(OutboxEvent outboxEvent, Guid userId)
        {
            using var payload = JsonDocument.Parse(outboxEvent.Payload);
            return outboxEvent.ResourceId == userId
                && payload.RootElement.TryGetProperty("kind", out var kind)
                && kind.GetString()?.EndsWith("Notice", StringComparison.Ordinal) == true;
        }
    }
}
