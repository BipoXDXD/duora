using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData("effect")]
    [InlineData("receipt")]
    [InlineData("review")]
    public async Task AFailureBetweenEffectReceiptAndReviewRollsEverythingBack(string stage)
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var table = stage switch
        {
            "receipt" => "safety.selfie_suspension_recoveries",
            "review" => "ops.outbox_deliveries",
            _ => "ops.audit_events",
        };
        var operation = stage == "review" ? "update" : "insert";
        var condition = stage == "effect" ? "new.action='safety.account_suspended'" : "true";
        await h.AdminSql($$"""
            create function ops.reject_synthetic_recovery() returns trigger language plpgsql as $$
            begin
              if {{condition}} then raise exception 'Synthetic recovery failure' using errcode='23514'; end if;
              return new;
            end $$;
            create trigger reject_synthetic_recovery before {{operation}} on {{table}}
              for each row execute function ops.reject_synthetic_recovery();
            """);
        var version = await h.Count("select coalesce(max(security_version),0) from safety.user_guards where user_id=$1", h.User);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Recover(h, actor.UserId, "rollback"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await AssertUntouched(h);
        Assert.Equal(0, await h.Count("select count(*) from safety.selfie_suspension_recoveries"));
        Assert.Equal(0, await h.Count("select count(*) from ops.audit_events where action in ('safety.account_suspended','ops.outbox_failure_resolved')"));
        Assert.Equal(version, await h.Count("select coalesce(max(security_version),0) from safety.user_guards where user_id=$1", h.User));
        await h.AdminSql($"drop trigger reject_synthetic_recovery on {table}");
        Assert.Equal("Suspended", (await Recover(h, actor.UserId, "rollback")).Result!.Disposition);
    }

    [Fact]
    public async Task TwoModeratorsSerializeOneRecoveryAndNeverOverwriteItsActor()
    {
        await using var h = await Harness.Create(postgres);
        var (firstActor, firstBrowser) = await h.Scenario.PrivilegedBrowserAsync();
        var (secondActor, secondBrowser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = firstBrowser;
        using var __ = secondBrowser;
        await using var blocker = new NpgsqlConnection(h.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from identity.users where id=$1 for update", Ct, h.User);
        var first = Recover(h, firstActor.UserId, "same-incident", "recovery-first");
        await WaitForWaiter(h, "recovery-first");
        var second = Recover(h, secondActor.UserId, "same-incident", "recovery-second");
        await WaitForWaiter(h, "recovery-second");

        await hold.CommitAsync(Ct);

        Assert.Equal("Suspended", (await first).Result!.Disposition);
        Assert.Equal("AlreadyReviewed", (await second).Code);
        Assert.Equal(1, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from safety.selfie_suspension_recoveries where moderator_user_id=$1", firstActor.UserId));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_resolved' and actor_id=$1", firstActor.UserId));
    }

    [Fact]
    public async Task ApprovalClearingTheLedgerWhileRecoveryWaitsCannotBeUndone()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(h.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var approval = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from identity.users where id=$1 for update", Ct, h.User);
        var recovery = Recover(h, actor.UserId, "approval-race", "recovery-approval");
        await WaitForWaiter(h, "recovery-approval");
        await Execute(blocker, "delete from identity.selfie_failures where user_id=$1", Ct, h.User);
        await approval.CommitAsync(Ct);

        Assert.Equal("LinkMissing", (await recovery).Code);
        Assert.Equal("PendingEligibility", await h.Text("select status from identity.users where id=$1", h.User));
        Assert.Equal(0, await h.Count("select count(*) from safety.moderation_actions"));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is null", h.Delivery));
    }

    [Fact]
    public async Task LockContentionIsBusyAndTheSameCommandSucceedsAfterRelease()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(h.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from identity.users where id=$1 for update", Ct, h.User);
        Assert.Equal("Busy", (await Recover(h, actor.UserId, "busy")).Code);
        await hold.RollbackAsync(Ct);
        await AssertUntouched(h);
        Assert.Equal("Suspended", (await Recover(h, actor.UserId, "busy")).Result!.Disposition);
    }

    [Fact]
    public async Task DeletionStartingWhileRecoveryWaitsIsRecordedWithoutSuspension()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(h.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var deletion = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from identity.users where id=$1 for update", Ct, h.User);
        var recovery = Recover(h, actor.UserId, "deletion-race", "recovery-deletion");
        await WaitForWaiter(h, "recovery-deletion");
        await Execute(blocker, "update identity.users set status='DeletionPending' where id=$1", Ct, h.User);
        await deletion.CommitAsync(Ct);

        Assert.Equal("NotSuspendable", (await recovery).Result!.Disposition);
        Assert.Equal("DeletionPending", await h.Text("select status from identity.users where id=$1", h.User));
        Assert.Equal(0, await h.Count("select count(*) from safety.moderation_actions"));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is not null", h.Delivery));
    }

    [Fact]
    public async Task PermissionRevokedWhileTheCommandWaitsIsCheckedAgainInsideTheDomain()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await using var blocker = new NpgsqlConnection(h.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from identity.users where id=$1 for update", Ct, h.User);
        var recovery = Recover(h, actor.UserId, "revoked-race", "recovery-revoked");
        await WaitForWaiter(h, "recovery-revoked");
        await h.Sql("delete from identity.user_roles where user_id=$1", actor.UserId);
        await hold.CommitAsync(Ct);

        Assert.Equal("Forbidden", (await recovery).Code);
        await AssertUntouched(h);
        Assert.Equal(0, await h.Count("select count(*) from safety.selfie_suspension_recoveries"));
    }

    private static async Task<SelfieRecoveryOutcome> Recover(Harness h, Guid actor, string key,
        string application = "recovery-domain")
    {
        var session = Guid.Parse(await h.Text("select id::text from identity.login_sessions where user_id=$1 order by created_at desc limit 1", actor));
        var connectionString = new NpgsqlConnectionStringBuilder(h.ConnectionString) { ApplicationName = application }.ConnectionString;
        PostgresFixture.TrackScratchPool(connectionString);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        return await new AccountSuspensions(db, h.Time).SuspendAsModeratorAsync(new(actor, session),
            new SelfieRecoveryRequest(h.Delivery, "INC-319", "Revisão humana do incidente sintético.", key), Ct);
    }

    private static async Task WaitForWaiter(Harness h, string application)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = new NpgsqlConnection(h.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = Command(connection, """
                select exists(select 1 from pg_stat_activity where application_name=$1
                  and cardinality(pg_blocking_pids(pid))>0)
                """, application);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(10, timeout.Token);
        }
    }
}
