using System.Diagnostics;
using System.Net;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentLedgerClearOrDeletionIsObservedAfterTheAccountLock(bool deletion)
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(Ct);
        await using var transaction = await blocker.BeginTransactionAsync(Ct);
        await SqlAsync(blocker, "select 1 from identity.users where id=$1 for update", user);
        await SqlAsync(blocker, deletion
            ? "update identity.users set status='DeletionPending' where id=$1"
            : "delete from identity.selfie_failures where user_id=$1", user);

        var recovering = RecoverAsync(browser, user, delivery);
        await WaitForBlockedAsync(blocker.ProcessID);
        await transaction.CommitAsync(Ct);
        using var response = await recovering;

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict,
            deletion ? "ACCOUNT_NOT_SUSPENDABLE" : "SELFIE_RECOVERY_UNAVAILABLE");
        Assert.Equal(deletion ? "DeletionPending" : "PendingEligibility", await StatusAsync(user));
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Fact]
    public async Task AConcurrentReviewPreventsApplyingTheOldDelivery()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(Ct);
        await using var transaction = await blocker.BeginTransactionAsync(Ct);
        await SqlAsync(blocker, "update ops.outbox_deliveries set reviewed_at=now() where id=$1", delivery);

        var recovering = RecoverAsync(browser, user, delivery);
        await WaitForBlockedAsync(blocker.ProcessID);
        await transaction.CommitAsync(Ct);
        using var response = await recovering;

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_UNAVAILABLE");
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Fact]
    public async Task ContentionRollsBackTheWholeOperationAndTheSameKeyCanBeRetried()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var key = AccountSuspensionScenario.NewKey();
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(Ct);
        await using var transaction = await blocker.BeginTransactionAsync(Ct);
        await SqlAsync(blocker, "select 1 from ops.outbox_deliveries where id=$1 for update", delivery);

        using var busy = await RecoverAsync(browser, user, delivery, key);

        await ProblemAssert.IsProblemAsync(busy, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.NotNull(busy.Headers.RetryAfter);
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
        await transaction.CommitAsync(Ct);
        using var retry = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task TwoModeratorsRecoveringTheSameDeliveryCreateOneHumanMeasure()
    {
        var (user, delivery) = await DeadAsync();
        var (_, first) = await _scenario.PrivilegedBrowserAsync();
        using var _ = first;
        var (_, second) = await _scenario.PrivilegedBrowserAsync();
        using var __ = second;
        var responses = await Task.WhenAll(RecoverAsync(first, user, delivery), RecoverAsync(second, user, delivery));
        try
        {
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
            await ProblemAssert.IsProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict, "ACCOUNT_ALREADY_SUSPENDED");
            Assert.Equal(1, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
            Assert.Equal(1, await _scenario.CountAsync("select count(*) from ops.audit_events where action='safety.account_suspension_source_recorded' and target_id=$1", delivery));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private Task<long> RecoveryAuditCountAsync(Guid delivery) => _scenario.CountAsync("""
        select count(*) from ops.audit_events where action='safety.account_suspension_source_recorded' and target_id=$1
        """, delivery);

    private async Task AssertNoRecoveryAuditAsync(Guid delivery) => Assert.Equal(0, await RecoveryAuditCountAsync(delivery));

    // A barreira observa o bloqueador real no PostgreSQL; não depende de adivinhar a duração da requisição.
    private async Task WaitForBlockedAsync(int blocker)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await _scenario.CountAsync("select count(*) from pg_stat_activity where $1=any(pg_blocking_pids(pid))", blocker) > 0)
            {
                return;
            }
            await Task.Delay(10, Ct);
        }
        Assert.Fail("A recuperação não alcançou a barreira de lock do PostgreSQL.");
    }

    private static async Task SqlAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
