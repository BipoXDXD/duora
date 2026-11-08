using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.Time.Testing;
using static Duora.IntegrationTests.Identity.SelfieFailureLockoutHarness;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Trilha da selfie em <c>ops.audit_events</c> (issue #384, TM-92) no PostgreSQL real: aprovação e reprovação gravam o
/// evento na transação do resultado, só com ator, ação e alvo, sem imagem, score, código de feedback nem referência
/// do fornecedor; repetição de entrega não duplica; a suspensão pela décima reprovação aponta para a tentativa.
/// </summary>
public sealed class SelfieAuditTests(PostgresFixture postgres)
{
    private const string Passed = "identity.selfie_verification_passed";
    private const string Failed = "identity.selfie_verification_failed";

    /// <summary>Colunas de <c>ops.audit_events</c>: o teste quebra se alguém acrescentar um campo ao evento.</summary>
    private const string Columns =
        "action,actor_id,actor_kind,actor_label,correlation_id,id,occurred_at,target_id,target_type,target_version";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Fact]
    public async Task AnApprovalRecordsThePassedAttemptAndTheActivatedAccount()
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        var since = await LastEventIdAsync(scenario);

        var id = await lockout.AttemptAsync(host, account.UserId, SelfieState.Passed);

        Assert.Equal($"System|-|-|{Passed}|selfie_verification|{id:D}|{await VersionAsync(scenario, id)}",
            await EventAsync(scenario, Passed, id));
        Assert.Equal($"System|-|-|identity.account_activated|user|{account.UserId:D}|-",
            await EventAsync(scenario, "identity.account_activated", account.UserId));
        Assert.Equal(2, await CountAllAsync(scenario, id, account.UserId, since));
    }

    [Fact]
    public async Task AnApprovalOfAnAlreadyActiveAccountRecordsNoActivation()
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        Assert.Equal(1, await scenario.ScalarAsync(
            "update identity.users set status = 'Active' where id = $1 returning 1", account.UserId));
        var since = await LastEventIdAsync(scenario);

        var id = await lockout.AttemptAsync(host, account.UserId, SelfieState.Passed);

        Assert.Equal($"System|-|-|{Passed}|selfie_verification|{id:D}|{await VersionAsync(scenario, id)}",
            await EventAsync(scenario, Passed, id));
        Assert.Equal("sem evento", await EventAsync(scenario, "identity.account_activated", account.UserId));
        Assert.Equal(1, await CountAllAsync(scenario, id, account.UserId, since));
    }

    [Theory]
    [InlineData(SelfieState.RecaptureRequired)]
    [InlineData(SelfieState.Inconclusive)]
    public async Task ARejectionRecordsTheFailedAttemptOnly(SelfieState result)
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        var since = await LastEventIdAsync(scenario);

        var id = await lockout.AttemptAsync(host, account.UserId, result);

        Assert.Equal($"System|-|-|{Failed}|selfie_verification|{id:D}|{await VersionAsync(scenario, id)}",
            await EventAsync(scenario, Failed, id));
        Assert.Equal(1, await CountAllAsync(scenario, id, account.UserId, since));
    }

    [Fact]
    public async Task AnExpiredAttemptIsNeitherApprovalNorRejection()
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        var since = await LastEventIdAsync(scenario);

        var id = await lockout.AttemptAsync(host, account.UserId, SelfieState.Expired);

        Assert.Equal(0, await CountAllAsync(scenario, id, account.UserId, since));
    }

    [Fact]
    public async Task AResultUnderAnotherPolicyVersionIsNotRecordedAsARejection()
    {
        var provider = new ScriptedSelfieProvider(_time);
        var (lockout, scenario, host) = await NewAsync(provider);
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        var since = await LastEventIdAsync(scenario);
        provider.Then(SelfieState.RecaptureRequired, policy: "another-policy");
        var id = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, account.UserId)).Status.Id!.Value;
        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);
        _time.Advance(UntilResult);

        await SelfieConcurrencyHarness.DispatchAsync(host, Ct);

        Assert.Equal("Inconclusive", (await CurrentAsync(host, account.UserId)).State);
        Assert.Equal(0, await CountAllAsync(scenario, id, account.UserId, since));
    }

    [Theory]
    [InlineData(SelfieState.Passed)]
    [InlineData(SelfieState.RecaptureRequired)]
    public async Task TheEventCarriesOnlyActorActionAndTargetWithoutProviderData(SelfieState result)
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();

        var id = await lockout.AttemptAsync(host, account.UserId, result);

        var action = result == SelfieState.Passed ? Passed : Failed;
        Assert.Equal(Columns, await scenario.ScalarTextAsync("""
            select coalesce(string_agg(key, ',' order by key), 'sem evento')
            from ops.audit_events a, jsonb_object_keys(to_jsonb(a)) key
            where a.action = $1 and a.target_id = $2
            """, action, id));
        var row = await scenario.ScalarTextAsync(
            "select coalesce(max(to_jsonb(a)::text), 'sem evento') from ops.audit_events a where a.action = $1 and a.target_id = $2", action, id);
        Assert.DoesNotContain("CAPTURE_QUALITY", row, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedSelfieProvider.Policy, row, StringComparison.Ordinal);
        Assert.DoesNotContain(account.UserId.ToString("D"), row, StringComparison.Ordinal);
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = $1 and target_id = $2 and occurred_at = $3",
            action, id, _time.GetUtcNow() - BetweenAttempts));
    }

    [Theory]
    [InlineData(SelfieState.Passed)]
    [InlineData(SelfieState.RecaptureRequired)]
    public async Task AResultRolledBackAtCommitLeavesNoEventAndItsRedeliveryRecordsOne(SelfieState result)
    {
        var provider = new ScriptedSelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var lockout = new SelfieFailureLockoutHarness(connection, _time, provider);
        var fault = new SelfieCommitFault(afterCommit: false);
        await using var failing = lockout.Host(fault);
        var scenario = new IdentityScenario(connection, failing);
        var account = await scenario.CreateConfirmedAccountAsync();
        var since = await LastEventIdAsync(scenario);
        provider.Then(result);
        provider.Then(result);
        var id = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(failing, account.UserId)).Status.Id!.Value;
        await SelfieConcurrencyHarness.DispatchAsync(failing, Ct);
        _time.Advance(UntilResult);
        fault.Arm();

        await Assert.ThrowsAsync<SelfieSimulatedCrash>(() => SelfieConcurrencyHarness.DispatchAsync(failing, Ct));
        var afterRollback = await CountAllAsync(scenario, id, account.UserId, since);
        var stateAfterRollback = await scenario.ScalarTextAsync("select state from identity.selfie_verifications where id = $1", id);
        await using var restarted = lockout.Host();
        _time.Advance(TimeSpan.FromSeconds(31));
        await SelfieConcurrencyHarness.DispatchAsync(restarted, Ct);

        Assert.True(fault.Fired);
        Assert.Equal(0, afterRollback);
        Assert.Equal("Capturing", stateAfterRollback);
        var action = result == SelfieState.Passed ? Passed : Failed;
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = $1 and target_id = $2", action, id));
    }

    [Theory]
    [InlineData(SelfieState.Passed)]
    [InlineData(SelfieState.RecaptureRequired)]
    public async Task AResultRedeliveredAfterACrashAtCommitIsRecordedOnce(SelfieState result)
    {
        var provider = new ScriptedSelfieProvider(_time);
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var lockout = new SelfieFailureLockoutHarness(connection, _time, provider);
        var fault = new SelfieCommitFault(afterCommit: true);
        await using var crashing = lockout.Host(fault);
        var scenario = new IdentityScenario(connection, crashing);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.Then(result);
        var id = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(crashing, account.UserId)).Status.Id!.Value;
        await SelfieConcurrencyHarness.DispatchAsync(crashing, Ct);
        _time.Advance(UntilResult);
        fault.Arm();
        await Assert.ThrowsAsync<SelfieSimulatedCrash>(() => SelfieConcurrencyHarness.DispatchAsync(crashing, Ct));
        await using var restarted = lockout.Host();
        _time.Advance(TimeSpan.FromSeconds(31));

        await SelfieConcurrencyHarness.DispatchAsync(restarted, Ct);

        Assert.True(fault.Fired);
        var action = result == SelfieState.Passed ? Passed : Failed;
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from ops.audit_events where action = $1 and target_id = $2", action, id));
    }

    [Fact]
    public async Task TheTenthRejectionIsRecordedAndTheSuspensionPointsToIt()
    {
        var (lockout, scenario, host) = await NewAsync();
        await using var _ = host;
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.RejectAsync(host, account.UserId, 5);
        _time.Advance(TimeSpan.FromHours(1));
        await lockout.RejectAsync(host, account.UserId, 4);
        var tenth = await lockout.AttemptAsync(host, account.UserId, SelfieState.RecaptureRequired);

        Assert.Equal(1, await SuspendPendingAsync(host));
        Assert.Equal(0, await SuspendPendingAsync(host));

        Assert.Equal(10, await scenario.ScalarAsync("""
            select count(*) from ops.audit_events a join identity.selfie_verifications v on v.id = a.target_id
            where a.action = $1 and a.target_type = 'selfie_verification' and v.user_id = $2
            """, Failed, account.UserId));
        Assert.Equal($"System|-|-|safety.account_suspension_source_recorded|selfie_verification|{tenth:D}|-",
            await EventAsync(scenario, "safety.account_suspension_source_recorded", tenth));
        Assert.Equal(1, await scenario.ScalarAsync("""
            select count(*) from ops.audit_events a join safety.moderation_actions m on m.id = a.target_id
            where a.action = 'safety.account_suspended' and a.actor_kind = 'System' and m.subject_user_id = $1
            """, account.UserId));
    }

    private static Task<string> EventAsync(IdentityScenario scenario, string action, Guid targetId) =>
        scenario.ScalarTextAsync("""
            select coalesce(string_agg(concat_ws('|', actor_kind, coalesce(actor_id::text, '-'), coalesce(actor_label, '-'),
                action, target_type, target_id, coalesce(target_version::text, '-')), ';'), 'sem evento')
            from ops.audit_events where action = $1 and target_id = $2
            """, action, targetId);

    private static Task<long> VersionAsync(IdentityScenario scenario, Guid id) =>
        scenario.ScalarAsync("select version from identity.selfie_verifications where id = $1", id);

    /// <summary>
    /// Todo evento sobre a tentativa ou a conta, de qualquer ação, depois de <paramref name="since"/>: pega evento a mais,
    /// não só o esperado. O cadastro e a confirmação do e-mail ficam antes do corte.
    /// </summary>
    private static Task<long> CountAllAsync(IdentityScenario scenario, Guid attemptId, Guid userId, long since) =>
        scenario.ScalarAsync("select count(*) from ops.audit_events where id > $3 and (target_id = $1 or target_id = $2)",
            attemptId, userId, since);

    private static Task<long> LastEventIdAsync(IdentityScenario scenario) =>
        scenario.ScalarAsync("select coalesce(max(id), 0) from ops.audit_events");

    private async Task<(SelfieFailureLockoutHarness Lockout, IdentityScenario Scenario, DuoraWebFactory Host)> NewAsync(
        ScriptedSelfieProvider? provider = null)
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var lockout = new SelfieFailureLockoutHarness(connection, _time, provider ?? new ScriptedSelfieProvider(_time));
        var host = lockout.Host();
        return (lockout, new IdentityScenario(connection, host), host);
    }
}
