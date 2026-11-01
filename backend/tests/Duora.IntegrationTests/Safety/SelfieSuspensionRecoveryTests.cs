using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>Recuperação humana de entrega morta, por HTTP e PostgreSQL real (#319; INV-06 e INV-10).</summary>
[Trait("Acceptance", "T-10")]
public sealed partial class SelfieSuspensionRecoveryTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Reason = "Incidente sintético: recuperação revisada pelo moderador.";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;
    private readonly string _connectionString;
    private readonly CapturingLoggerProvider _logs = new();

    public SelfieSuspensionRecoveryTests(PostgresFixture postgres)
    {
        _connectionString = postgres.ConnectionString;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AModeratorRecoversAScrubbedDeliveryWithoutTheOriginalAttemptAndAuditsTheHumanDecision()
    {
        var (user, delivery) = await DeadAsync();
        var (actor, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var _ = moderator;

        using var response = await RecoverAsync(moderator, user, delivery);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await MfaScenario.ReadObjectAsync(response);
        var measure = Guid.Parse((string)body["suspensionId"]!);
        Assert.Equal("SelfieVerificationFailed", (string?)body["reasonCode"]);
        Assert.False((bool)body["decidedAutomatically"]!);
        Assert.DoesNotContain(Reason, body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Reason, string.Join('\n', _logs.Entries.Select(e => e.Text)), StringComparison.Ordinal);
        Assert.Equal("Suspended", await StatusAsync(user));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from safety.moderation_actions where id=$1 and author_kind='Human'
              and author_user_id=$2 and internal_reason=$3 and reason_code='SelfieVerificationFailed'
            """, measure, actor.UserId, Reason));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.audit_events where action='safety.account_suspended'
              and actor_kind='Moderator' and actor_id=$1 and target_id=$2
            """, actor.UserId, measure));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.audit_events where action='safety.account_suspension_source_recorded'
              and actor_kind='Moderator' and actor_id=$1 and target_type='outbox_delivery' and target_id=$2
            """, actor.UserId, delivery));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.outbox_deliveries d join ops.outbox_events e on e.id=d.outbox_event_id
            where d.id=$1 and d.status='Failed' and d.attempts=10 and d.reviewed_at is null and e.payload='{}'::jsonb
            """, delivery));
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from identity.selfie_verifications where user_id=$1", user));
    }

    [Theory]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task NonModeratorsAreRefusedBeforeBodyValidation(string role)
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync(role);
        using var _ = browser;
        using var response = await browser.PostRawJsonAsync(Path(user, delivery), "{invalid");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        await AssertNoMeasureAsync(user);
    }

    [Fact]
    public async Task AnExpiredStepUpCannotRecoverADelivery()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);

        using var response = await RecoverAsync(browser, user, delivery);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        await AssertNoMeasureAsync(user);
    }

    private static string Path(Guid user, Guid delivery) =>
        $"/api/v1/admin/accounts/{user}/selfie-suspension-recoveries/{delivery}";

    private static Task<HttpResponseMessage> RecoverAsync(Browser browser, Guid user, Guid delivery,
        string? key = null, string reason = Reason) =>
        browser.PostJsonAsync(Path(user, delivery), new { internalReason = reason },
            AccountSuspensionScenario.WithKey(key ?? AccountSuspensionScenario.NewKey()));

    private Task<string> StatusAsync(Guid user) =>
        _scenario.TextAsync("select status from identity.users where id=$1", user);

    private async Task AssertNoMeasureAsync(Guid user)
    {
        Assert.Equal("PendingEligibility", await StatusAsync(user));
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
    }

    private async Task<(Guid User, Guid Delivery)> DeadAsync()
    {
        var account = await _scenario.Identity.CreateConfirmedAccountAsync();
        var verification = Guid.NewGuid();
        var now = _time.GetUtcNow();
        await _scenario.ExecuteAsync("""
            insert into identity.selfie_failures (verification_id,user_id,failed_at)
            select case when n=10 then $2 else gen_random_uuid() end,$1,$3::timestamptz-interval '40 days'+n*interval '1 hour'
            from generate_series(1,10) n
            """, account.UserId, verification, now);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var item = SelfieAttemptsSuspensionOutbox.Create(account.UserId, verification, now - TimeSpan.FromDays(40));
        db.Add(item);
        await db.SaveChangesAsync(Ct);
        var delivery = item.Deliveries.Single().Id;
        await _scenario.ExecuteAsync("""
            update ops.outbox_deliveries set status='Failed',attempts=10,completed_at=$2 where id=$1
            """, delivery, now);
        await _scenario.ExecuteAsync("update ops.outbox_events set payload='{}'::jsonb where id=$1", item.Id);
        return (account.UserId, delivery);
    }
}
