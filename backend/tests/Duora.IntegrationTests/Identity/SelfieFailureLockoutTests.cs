using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Duora.IntegrationTests.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.IntegrationTests.Identity.SelfieFailureLockoutHarness;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Limite de reprovações da selfie (issue #300) no PostgreSQL real: contagem durável e idempotente por tentativa,
/// pausa de 1 hora aplicada no servidor com fronteira exata e suspensão automática pela outbox, fora da transação do
/// resultado, repetida até gravar.
/// </summary>
public sealed class SelfieFailureLockoutTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Fact]
    public async Task EachRejectionIsRecordedOnceWithTheInstantOfTheResult()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();

        var id = await lockout.AttemptAsync(host, account.UserId, SelfieState.Inconclusive);

        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.selfie_failures where user_id = $1 and verification_id = $2 and failed_at = $3",
            account.UserId, id, _time.GetUtcNow() - BetweenAttempts));
        var current = await CurrentAsync(host, account.UserId);
        Assert.Equal(new SelfieAttemptStanding.Available(4, LastCycle: false), current.Attempts);
    }

    [Theory]
    [InlineData(SelfieState.Expired)]
    [InlineData(SelfieState.Passed)]
    public async Task ExpiryAndApprovalAreNotRejections(SelfieState result)
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();

        await lockout.AttemptAsync(host, account.UserId, result);

        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task AProviderOutageIsRetriedAndNeverCounted()
    {
        var provider = new ScriptedSelfieProvider(_time);
        var (lockout, connection) = await NewAsync(provider);
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.ThenUnavailable();
        provider.Then(SelfieState.RecaptureRequired);
        Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, account.UserId));
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilResult);

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        var afterOutage = await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId);
        _time.Advance(UntilResult);
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal(0, afterOutage);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
        Assert.Equal(2, provider.ReadCalls);
    }

    [Fact]
    public async Task AResultUnderAnotherPolicyVersionEndsInconclusiveWithoutCounting()
    {
        var provider = new ScriptedSelfieProvider(_time);
        var (lockout, connection) = await NewAsync(provider);
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.Then(SelfieState.RecaptureRequired, policy: "another-policy");
        Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, account.UserId));
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilResult);

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Inconclusive", (await CurrentAsync(host, account.UserId)).State);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task ARedeliveredResultAfterACrashAtCommitIsCountedOnce()
    {
        var provider = new ScriptedSelfieProvider(_time);
        var (lockout, connection) = await NewAsync(provider);
        var fault = new SelfieCommitFault(afterCommit: true);
        await using var crashing = lockout.Host(fault);
        var scenario = new IdentityScenario(connection, crashing);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.Then(SelfieState.RecaptureRequired);
        Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(crashing, account.UserId));
        await SelfieConcurrencyHarness.DispatchAsync(crashing, Ct);
        _time.Advance(UntilResult);
        fault.Arm();
        await Assert.ThrowsAsync<SelfieSimulatedCrash>(() => SelfieConcurrencyHarness.DispatchAsync(crashing, Ct));
        await using var restarted = lockout.Host();
        _time.Advance(TimeSpan.FromSeconds(31));

        await SelfieConcurrencyHarness.DispatchAsync(restarted, Ct);

        Assert.True(fault.Fired);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-provider' and status = 'Delivered'"));
    }

    [Fact]
    public async Task TwoWorkersReadingTheSameResultInParallelCountItOnce()
    {
        var barrier = new SelfieCallBarrier();
        var provider = new ScriptedSelfieProvider(_time) { FirstReadBarrier = barrier };
        var (lockout, connection) = await NewAsync(provider);
        await using var first = lockout.Host();
        await using var second = lockout.Host();
        var scenario = new IdentityScenario(connection, first);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.Then(SelfieState.RecaptureRequired);
        provider.Then(SelfieState.RecaptureRequired);
        Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(first, account.UserId));
        await SelfieConcurrencyHarness.DispatchAsync(first, Ct);
        _time.Advance(UntilResult);
        _ = second.Services;

        var stale = SelfieConcurrencyHarness.DispatchAsync(first, Ct);
        try
        {
            await barrier.WaitForEntryAsync(Ct);
            _time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(1, await SelfieConcurrencyHarness.DispatchAsync(second, Ct));
        }
        finally
        {
            barrier.Release();
            await stale;
        }

        Assert.Equal(2, provider.ReadCalls);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task TheLedgerRefusesASecondRowForTheSameAttemptEvenWithoutTheService()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        var id = await lockout.AttemptAsync(host, account.UserId, SelfieState.RecaptureRequired);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scenario.ScalarAsync(
            "insert into identity.selfie_failures (verification_id, user_id, failed_at) values ($1, $2, now()) returning 1",
            id, account.UserId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Fact]
    public async Task RecordingTheSameAttemptFromParallelTransactionsKeepsOneRowWithoutError()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        var attempt = Guid.NewGuid();

        var recorded = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
            return await SelfieFailureLedger.RecordAsync(db, attempt, account.UserId, _time.GetUtcNow(), Ct);
        }));

        Assert.Equal(1, recorded.Count(isNew => isNew));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task TheMigrationAloneKeepsTheAppFromRewritingARecordedRejection()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using var owner = new NpgsqlConnection(database.MigratorConnectionString);
        await owner.OpenAsync(Ct);
        await using var privileges = new NpgsqlCommand("""
            select array[has_table_privilege('duora_app', 'identity.selfie_failures', 'SELECT'),
                         has_table_privilege('duora_app', 'identity.selfie_failures', 'INSERT'),
                         has_table_privilege('duora_app', 'identity.selfie_failures', 'DELETE'),
                         has_table_privilege('duora_app', 'identity.selfie_failures', 'UPDATE'),
                         has_table_privilege('duora_app', 'identity.selfie_failures', 'TRUNCATE')]
            """, owner);

        var granted = (bool[])(await privileges.ExecuteScalarAsync(Ct))!;

        Assert.Equal("SELECT INSERT DELETE", string.Join(' ', granted.Zip(["SELECT", "INSERT", "DELETE", "UPDATE", "TRUNCATE"])
            .Where(pair => pair.First).Select(pair => pair.Second)));
    }

    [Fact]
    public async Task FiveRejectionsLockTheSelfieUntilExactlyOneHourAfterTheFifth()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 5);
        var fifth = _time.GetUtcNow() - BetweenAttempts;
        var opensAt = fifth + TimeSpan.FromHours(1);
        _time.SetUtcNow(opensAt - TimeSpan.FromTicks(1));

        var locked = await RequestAsync(host, account.UserId);
        var lockedStatus = await CurrentAsync(host, account.UserId);
        _time.SetUtcNow(opensAt);
        var reopened = await RequestAsync(host, account.UserId);

        Assert.Equal(new SelfieRequestOutcome.AttemptsLocked(opensAt), locked);
        Assert.Equal(new SelfieAttemptStanding.CoolingDown(opensAt), lockedStatus.Attempts);
        var accepted = Assert.IsType<SelfieRequestOutcome.Accepted>(reopened);
        Assert.Equal(new SelfieAttemptStanding.Available(5, LastCycle: true), accepted.Status.Attempts);
        Assert.Equal(6, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
    }

    [Fact]
    public async Task TheLockedRequestAnswers429WithTheStableCodeAndTheSecondsUntilTheSecondCycle()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 5);
        var opensAt = _time.GetUtcNow() - BetweenAttempts + TimeSpan.FromHours(1);
        _time.SetUtcNow(opensAt - TimeSpan.FromSeconds(90));
        using var browser = await scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync("/api/v1/me/selfie-verifications", new { });
        using var current = await browser.GetAsync("/api/v1/me/selfie-verifications/current");
        using var me = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.TooManyRequests, "SELFIE_ATTEMPTS_LOCKED");
        Assert.Equal(90, ProblemAssert.RetryAfterSeconds(response));
        var attempts = (await MfaScenario.ReadObjectAsync(current))["attempts"]!.AsObject();
        Assert.Equal(["state", "remaining", "lastCycle", "lockedUntil"], attempts.Select(p => p.Key));
        Assert.Equal("CoolingDown", (string?)attempts["state"]);
        Assert.Equal(0, (int?)attempts["remaining"]);
        Assert.Equal(opensAt, DateTimeOffset.Parse((string)attempts["lockedUntil"]!, System.Globalization.CultureInfo.InvariantCulture));
        var meAttempts = (await MfaScenario.ReadObjectAsync(me))["selfieAttempts"]!.AsObject();
        Assert.Equal("CoolingDown", (string?)meAttempts["state"]);
        Assert.Equal(5, await scenario.ScalarAsync("select count(*) from identity.selfie_verifications where user_id = $1", account.UserId));
    }

    [Theory]
    [InlineData(Duora.Core.Identity.AccountRoles.Moderator)]
    [InlineData(Duora.Core.Identity.AccountRoles.ProductAdmin)]
    [InlineData(Duora.Core.Identity.AccountRoles.Operator)]
    public async Task APrivilegedAccountHasNoSelfieAttemptsInMeNorInTheCurrentSelfie(string role)
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var (privileged, browser) = await suspensions.PrivilegedBrowserAsync(role);
        using var __ = browser;

        using var me = await browser.GetAsync("/api/v1/me");
        using var current = await browser.GetAsync("/api/v1/me/selfie-verifications/current");
        var status = await CurrentAsync(host, privileged.UserId);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False((await MfaScenario.ReadObjectAsync(me)).ContainsKey("selfieAttempts"));
        // A rota da selfie é de participante (403 para conta privilegiada); o serviço também não calcula tentativas.
        var problem = await ProblemAssert.IsProblemAsync(current, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.False(problem.ContainsKey("attempts"));
        Assert.Null(status.Attempts);
    }

    [Fact]
    public async Task AParticipantSeesTheAttemptsInMeAndInTheCurrentSelfie()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        using var me = await browser.GetAsync("/api/v1/me");
        using var current = await browser.GetAsync("/api/v1/me/selfie-verifications/current");

        Assert.Equal("Available", (string?)(await MfaScenario.ReadObjectAsync(me))["selfieAttempts"]?["state"]);
        Assert.Equal("Available", (string?)(await MfaScenario.ReadObjectAsync(current))["attempts"]?["state"]);
    }

    [Fact]
    public async Task AnApprovalInTheMiddleEndsTheCount()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 3);

        await lockout.AttemptAsync(host, account.UserId, SelfieState.Passed);

        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
        var again = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, account.UserId));
        Assert.Equal("Passed", again.Status.State);
    }

    [Fact]
    public async Task TheTenthRejectionExhaustsTheAttemptsAndSuspendsTheAccountOnce()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 5);
        _time.Advance(TimeSpan.FromHours(1));
        await lockout.RejectAsync(host, account.UserId, 4);
        Assert.Equal(new SelfieAttemptStanding.Available(1, LastCycle: true), (await CurrentAsync(host, account.UserId)).Attempts);
        var tenth = await lockout.AttemptAsync(host, account.UserId, SelfieState.RecaptureRequired);
        var exhausted = await RequestAsync(host, account.UserId);
        var beforeWorker = await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId);

        Assert.Equal(1, await SuspendPendingAsync(host));
        Assert.Equal(0, await SuspendPendingAsync(host));

        Assert.IsType<SelfieRequestOutcome.AttemptsExhausted>(exhausted);
        Assert.Equal("PendingEligibility", beforeWorker);
        Assert.Equal("Suspended", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("""
            select count(*) from safety.moderation_actions
            where subject_user_id = $1 and reason_code = 'SelfieVerificationFailed' and author_kind = 'Automated'
              and lifted_at is null
            """, account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("""
            select count(*) from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
            where d.consumer = 'selfie-attempts-suspension' and d.status = 'Delivered' and e.resource_id = $1
            """, tenth));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspended' and actor_kind = 'System'"));
    }

    [Fact]
    public async Task ASuspensionRedeliveredAfterItWasRecordedKeepsOneMeasure()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await ExhaustedAccountAsync(lockout, host, scenario);
        Assert.Equal(1, await SuspendPendingAsync(host));
        await RedeliverSuspensionAsync(scenario, account.UserId, await LastVerificationAsync(scenario, account.UserId));

        Assert.Equal(1, await SuspendPendingAsync(host));

        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from safety.moderation_actions where subject_user_id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-attempts-suspension' and status = 'Delivered'"));
    }

    [Fact]
    public async Task ASuspensionRedeliveredAfterTheModeratorLiftedItDoesNotSuspendAgain()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await ExhaustedAccountAsync(lockout, host, scenario);
        var tenth = await LastVerificationAsync(scenario, account.UserId);
        Assert.Equal(1, await SuspendPendingAsync(host));
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        using var state = await moderator.GetAsync(AccountSuspensionScenario.StatePath(account.UserId));
        var suspension = (await MfaScenario.ReadObjectAsync(state))["activeSuspension"]!.AsObject();
        using (var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(account.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisão humana." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!)))
        {
            Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        }

        await RedeliverSuspensionAsync(scenario, account.UserId, tenth);
        Assert.Equal(1, await SuspendPendingAsync(host));

        Assert.Equal("PendingEligibility", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from safety.moderation_actions where subject_user_id = $1", account.UserId));
    }

    [Fact]
    public async Task ASuspensionBlockedByALockIsRetriedUntilItIsRecorded()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await ExhaustedAccountAsync(lockout, host, scenario);
        await scenario.ScalarAsync("insert into safety.user_guards (user_id) values ($1) on conflict do nothing returning 1", account.UserId);
        await using (var holder = new NpgsqlConnection(connection))
        {
            await holder.OpenAsync(Ct);
            await using var hold = await holder.BeginTransactionAsync(Ct);
            await using (var lockGuard = new NpgsqlCommand("select 1 from safety.user_guards where user_id = $1 for update", holder, hold))
            {
                lockGuard.Parameters.AddWithValue(account.UserId);
                await lockGuard.ExecuteScalarAsync(Ct);
            }

            await SuspendPendingAsync(host);
            await hold.RollbackAsync(Ct);
        }

        var whileLocked = await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId);
        var pending = await scenario.ScalarAsync(
            "select count(*) from ops.outbox_deliveries where consumer = 'selfie-attempts-suspension' and status = 'Pending' and last_error = 'Busy'");
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, await SuspendPendingAsync(host));

        Assert.Equal("PendingEligibility", whileLocked);
        Assert.Equal(1, pending);
        Assert.Equal("Suspended", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
    }

    [Fact]
    public async Task ASuspensionLostWithTheWorkerIsTakenAgainAfterTheLease()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var scenario = new IdentityScenario(connection, host);
        var account = await ExhaustedAccountAsync(lockout, host, scenario);
        await scenario.ScalarAsync("""
            update ops.outbox_deliveries set status = 'InFlight', attempts = 1, fencing_token = fencing_token + 1,
              lease_expires_at = $1
            where consumer = 'selfie-attempts-suspension' returning 1
            """, _time.GetUtcNow() + TimeSpan.FromSeconds(30));

        var whileLeased = await SuspendPendingAsync(host);
        _time.Advance(TimeSpan.FromSeconds(30));
        var afterLease = await SuspendPendingAsync(host);

        Assert.Equal(0, whileLeased);
        Assert.Equal(1, afterLease);
        Assert.Equal("Suspended", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
    }

    [Fact]
    public async Task LiftingTheSelfieSuspensionGivesANewFirstCycle()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await ExhaustedAccountAsync(lockout, host, scenario);
        Assert.Equal(1, await SuspendPendingAsync(host));
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        using var state = await moderator.GetAsync(AccountSuspensionScenario.StatePath(account.UserId));
        var suspension = (await MfaScenario.ReadObjectAsync(state))["activeSuspension"]!.AsObject();
        Assert.True((bool)suspension["decidedAutomatically"]!);
        Assert.Equal("SelfieVerificationFailed", (string?)suspension["reasonCode"]);

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(account.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisão humana: nova chance para a selfie." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
        Assert.Equal("PendingEligibility", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
        // O orçamento de 5 pedidos por hora (spec 12.2) continua valendo depois da revogação.
        _time.Advance(TimeSpan.FromHours(1));
        var accepted = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, account.UserId));
        Assert.Equal(new SelfieAttemptStanding.Available(5, LastCycle: false), accepted.Status.Attempts);
    }

    [Fact]
    public async Task LiftingAnotherSuspensionKeepsTheSelfieCount()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 2);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, account.UserId);

        using var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(account.UserId, Guid.Parse((string)suspension["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)suspension["eTag"]!));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal(2, await scenario.ScalarAsync("select count(*) from identity.selfie_failures where user_id = $1", account.UserId));
    }

    private async Task<TestAccount> ExhaustedAccountAsync(
        SelfieFailureLockoutHarness lockout,
        DuoraWebFactory host,
        IdentityScenario scenario)
    {
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 5);
        _time.Advance(TimeSpan.FromHours(1));
        await lockout.RejectAsync(host, account.UserId, 5);
        return account;
    }

    /// <summary>Reentrega at-least-once: a entrega volta à fila com o payload original (a outbox o limpa ao concluir).</summary>
    private async Task RedeliverSuspensionAsync(IdentityScenario scenario, Guid userId, Guid verificationId)
    {
        await scenario.ScalarAsync("""
            update ops.outbox_deliveries set status = 'Pending', completed_at = null, available_at = $1
            where consumer = 'selfie-attempts-suspension' returning 1
            """, _time.GetUtcNow());
        await scenario.ScalarAsync("""
            update ops.outbox_events set payload = $1::jsonb
            where id = (select outbox_event_id from ops.outbox_deliveries where consumer = 'selfie-attempts-suspension')
            returning 1
            """, $"{{\"userId\":\"{userId:D}\",\"verificationId\":\"{verificationId:D}\"}}");
    }

    private static async Task<Guid> LastVerificationAsync(IdentityScenario scenario, Guid userId) =>
        Guid.Parse(await scenario.ScalarTextAsync(
            "select verification_id::text from identity.selfie_failures where user_id = $1 order by failed_at desc limit 1",
            userId));

    private async Task<(SelfieFailureLockoutHarness Lockout, string Connection)> NewAsync(ScriptedSelfieProvider? provider = null)
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        return (new SelfieFailureLockoutHarness(connection, _time, provider ?? new ScriptedSelfieProvider(_time)), connection);
    }
}
