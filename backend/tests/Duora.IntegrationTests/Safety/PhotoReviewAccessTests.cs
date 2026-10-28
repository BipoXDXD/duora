using System.Data.Common;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed class PhotoReviewAccessTests(PostgresFixture postgres)
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Assign")]
    [InlineData("Evidence")]
    [InlineData("Decide")]
    [InlineData("Preserve")]
    public async Task EachCapabilityRequiresItsOwnAuditedGrantAndRevocation(string capabilityName)
    {
        var capability = Enum.Parse<PhotoReviewCapability>(capabilityName);
        var actor = await ReadyActorAsync();
        Assert.False(await AuthorizedAsync(actor, capability));
        Assert.True(await ChangeAsync(actor.UserId, capability, enabled: true));
        Assert.True(await AuthorizedAsync(actor, capability));
        foreach (var other in Enum.GetValues<PhotoReviewCapability>().Where(value => value != capability))
        {
            Assert.False(await AuthorizedAsync(actor, other));
        }

        Assert.False(await ChangeAsync(actor.UserId, capability, enabled: true));
        Assert.True(await ChangeAsync(actor.UserId, capability, enabled: false));
        Assert.False(await AuthorizedAsync(actor, capability));
        Assert.False(await ChangeAsync(actor.UserId, capability, enabled: false));
        Assert.True(await ChangeAsync(actor.UserId, capability, enabled: true));
        Assert.True(await AuthorizedAsync(actor, capability));
        Assert.Equal(3, await ScalarAsync("select count(*) from safety.photo_review_grants where user_id=$1", actor.UserId));
        Assert.Equal(3, await ScalarAsync("select max(version) from safety.photo_review_grants where user_id=$1", actor.UserId));
        Assert.Equal(3, await ScalarAsync("""
            select count(*) from ops.audit_events a join safety.photo_review_grants g on g.id=a.target_id
            where g.user_id=$1 and a.target_type='photo_review_grant' and a.actor_kind='TerminalOperator'
              and a.actor_label='review-test' and a.action in
                ('safety.photo_review_permission_granted','safety.photo_review_permission_revoked')
            """, actor.UserId));
    }

    [Theory]
    [InlineData(AccountRoles.Operator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Participant)]
    public async Task OtherRolesCannotReceiveReviewPermissionImplicitly(string role)
    {
        var actor = await ReadyActorAsync(role);
        Assert.False(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
    }

    [Theory]
    [InlineData("restricted")]
    [InlineData("unconfirmed")]
    [InlineData("mfa-disabled")]
    [InlineData("temporary-password")]
    [InlineData("role-removed")]
    public async Task RevocationStillWorksAfterTheAccountLosesEligibility(string condition)
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await InvalidateAsync(actor, condition);
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: false));
        Assert.False(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
        Assert.Equal(2, await ScalarAsync("select count(*) from safety.photo_review_grants where user_id=$1", actor.UserId));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("idle-expired")]
    [InlineData("absolute-expired")]
    [InlineData("stamp-changed")]
    [InlineData("no-mfa-proof")]
    [InlineData("future-mfa-proof")]
    [InlineData("normal-policy")]
    [InlineData("restricted")]
    [InlineData("unconfirmed")]
    [InlineData("mfa-disabled")]
    [InlineData("temporary-password")]
    [InlineData("role-removed")]
    public async Task AuthorizationRevalidatesLiveAccountAndSession(string condition)
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        Assert.True(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
        await InvalidateAsync(actor, condition);
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
    }

    [Fact]
    public async Task ASessionIdentifierDoesNotAuthorizeAnotherAccount()
    {
        var actor = await ReadyActorAsync();
        var other = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        Assert.False(await AuthorizedAsync(actor with { SessionId = other.SessionId }, PhotoReviewCapability.Evidence));
        Assert.False(await AuthorizedAsync(actor with { SessionId = Guid.NewGuid() }, PhotoReviewCapability.Evidence));
    }

    [Theory]
    [InlineData("Decide", -6)]
    [InlineData("Decide", -5)]
    [InlineData("Decide", 1)]
    [InlineData("Preserve", -6)]
    [InlineData("Preserve", -5)]
    [InlineData("Preserve", 1)]
    public async Task CriticalAuthorizationRequiresRecentTotpAndNotOnlyRecoveryCode(string capabilityName, int minutes)
    {
        var capability = Enum.Parse<PhotoReviewCapability>(capabilityName);
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, capability, enabled: true));
        await ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1", actor.SessionId, _time.GetUtcNow().AddMinutes(minutes));
        Assert.False(await AuthorizedAsync(actor, capability));
        await ExecuteAsync("update identity.login_sessions set totp_verified_at=null where id=$1", actor.SessionId);
        Assert.False(await AuthorizedAsync(actor, capability));
        await ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1", actor.SessionId, _time.GetUtcNow().AddMinutes(-1));
        Assert.True(await AuthorizedAsync(actor, capability));
    }

    [Fact]
    public async Task AuthorizationCannotBeDetachedFromTheEffectsTransaction()
    {
        var actor = await ReadyActorAsync();
        await using var db = postgres.CreateDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PhotoReviewAccess(db, _time)
            .AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct));
    }

    [Fact]
    public async Task AuditFailureRollsBackTheGrant()
    {
        var actor = await ReadyActorAsync();
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(postgres.ConnectionString).AddInterceptors(new RefuseGrantAudit());
        await using var db = new DuoraDbContext(options.Options);
        Assert.True(OperatorName.TryParse("review-test", out var operatorName));

        await Assert.ThrowsAsync<IOException>(() => new PhotoReviewAccess(db, _time)
            .SetCapabilityAsync(actor.UserId, PhotoReviewCapability.Evidence, true, operatorName, Ct));

        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_grants where user_id=$1", actor.UserId));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("account")]
    [InlineData("role")]
    public async Task AuthorizationHoldsLiveIdentityRowsUntilTheEffectCommits(string row)
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        Assert.True(await new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct));
        await using var competing = new NpgsqlConnection(postgres.ConnectionString);
        await competing.OpenAsync(Ct);
        await using var competingTransaction = await competing.BeginTransactionAsync(Ct);
        await using (var timeout = new NpgsqlCommand("set local lock_timeout='150ms'", competing, competingTransaction))
        {
            await timeout.ExecuteNonQueryAsync(Ct);
        }

        var statement = row switch
        {
            "session" => "update identity.login_sessions set revoked_at=now(),revocation_reason='test' where user_id=$1",
            "account" => "update identity.users set status='Restricted' where id=$1",
            _ => "delete from identity.user_roles where user_id=$1",
        };
        await using (var blocked = new NpgsqlCommand(statement, competing, competingTransaction))
        {
            blocked.Parameters.AddWithValue(actor.UserId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => blocked.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }

        await competingTransaction.RollbackAsync(Ct);
        await transaction.CommitAsync(Ct);
        await ExecuteAsync(statement, actor.UserId);
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
    }

    [Fact]
    public async Task RoleUpdateUsingTheD13TriggerCannotFormALockCycleWithAuthorization()
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={actor.UserId} for share", Ct);
        await using var competing = new NpgsqlConnection(postgres.ConnectionString);
        await competing.OpenAsync(Ct);
        await using var competingTransaction = await competing.BeginTransactionAsync(Ct);
        await using (var roleLock = new NpgsqlCommand("select 1 from identity.user_roles where user_id=$1 for update", competing, competingTransaction))
        {
            roleLock.Parameters.AddWithValue(actor.UserId);
            await roleLock.ExecuteNonQueryAsync(Ct);
        }

        await using var update = new NpgsqlCommand("""
            update identity.user_roles set role_id=(select id from identity.roles where name='product_admin')
            where user_id=$1
            """, competing, competingTransaction);
        update.Parameters.AddWithValue(actor.UserId);
        using var updateBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var updating = update.ExecuteNonQueryAsync(updateBudget.Token);
        try
        {
            var waiting = false;
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(observation.Token);
            while (!waiting)
            {
                await using var query = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=$1 and wait_event_type='Lock')", observer);
                query.Parameters.AddWithValue(competing.ProcessID);
                waiting = (bool)(await query.ExecuteScalarAsync(observation.Token))!;
                if (!waiting)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), observation.Token);
                }
            }

            var error = await Assert.ThrowsAsync<PostgresException>(() => new PhotoReviewAccess(db, _time)
                .AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(cleanup.Token);
            try
            {
                await updating.WaitAsync(cleanup.Token);
            }
            finally
            {
                await competingTransaction.RollbackAsync(cleanup.Token);
            }
        }
    }

    [Fact]
    public async Task AppendingARevocationWaitsForTheAuthorizedEffectsTransaction()
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        Assert.True(await new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct));
        await using var revoker = postgres.CreateDbContext();
        await revoker.Database.OpenConnectionAsync(Ct);
        var pid = ((NpgsqlConnection)revoker.Database.GetDbConnection()).ProcessID;
        Assert.True(OperatorName.TryParse("review-test", out var operatorName));
        var revoking = new PhotoReviewAccess(revoker, _time)
            .SetCapabilityAsync(actor.UserId, PhotoReviewCapability.Evidence, false, operatorName, Ct);
        var committed = false;
        try
        {
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(observation.Token);
            var waiting = false;
            while (!waiting)
            {
                await using var query = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=$1 and wait_event_type='Lock')", observer);
                query.Parameters.AddWithValue(pid);
                waiting = (bool)(await query.ExecuteScalarAsync(observation.Token))!;
                if (!waiting)
                {
                    Assert.False(revoking.IsCompleted, "Revocation must wait on the authorized transaction, not append a grant concurrently.");
                    await Task.Delay(TimeSpan.FromMilliseconds(10), observation.Token);
                }
            }

            Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_grants where user_id=$1", actor.UserId));
            await transaction.CommitAsync(Ct);
            committed = true;
            Assert.True(await revoking.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!committed)
            {
                await transaction.RollbackAsync(cleanup.Token);
            }
            await revoking.WaitAsync(cleanup.Token);
        }

        Assert.Equal(2, await ScalarAsync("select count(*) from safety.photo_review_grants where user_id=$1", actor.UserId));
        Assert.False(await AuthorizedAsync(actor, PhotoReviewCapability.Evidence));
    }

    [Fact]
    public async Task SessionExpiryWhileWaitingForAuthorizationIsCheckedAfterTheLock()
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await using var guard = new NpgsqlConnection(postgres.ConnectionString);
        await guard.OpenAsync(Ct);
        await using var guardTransaction = await guard.BeginTransactionAsync(Ct);
        await using (var takeGuard = new NpgsqlCommand("""
            select 1 from safety.user_guards where user_id=$1 for update
            """, guard, guardTransaction))
        {
            takeGuard.Parameters.AddWithValue(actor.UserId);
            Assert.Equal(1, await ScalarAsync("select count(*) from safety.user_guards where user_id=$1", actor.UserId));
            await takeGuard.ExecuteNonQueryAsync(Ct);
        }

        await using var db = postgres.CreateDbContext();
        await db.Database.OpenConnectionAsync(Ct);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var authorizing = new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct);
        var released = false;
        try
        {
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(observation.Token);
            var waiting = false;
            while (!waiting)
            {
                await using var query = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=$1 and wait_event_type='Lock')", observer);
                query.Parameters.AddWithValue(pid);
                waiting = (bool)(await query.ExecuteScalarAsync(observation.Token))!;
                if (!waiting)
                {
                    Assert.False(authorizing.IsCompleted);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), observation.Token);
                }
            }

            _time.Advance(TimeSpan.FromMinutes(11));
            await guardTransaction.CommitAsync(Ct);
            released = true;
            Assert.False(await authorizing.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!released)
            {
                await guardTransaction.RollbackAsync(cleanup.Token);
            }
            await authorizing.WaitAsync(cleanup.Token);
        }
    }

    [Fact]
    public async Task SnapshotIsolationCannotReuseAGrantFromBeforeRevocation()
    {
        var actor = await ReadyActorAsync();
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: true));
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Ct);
        _ = await db.Database.SqlQuery<int>($"select 1 as \"Value\" from identity.users where id={actor.UserId}").SingleAsync(Ct);
        Assert.True(await ChangeAsync(actor.UserId, PhotoReviewCapability.Evidence, enabled: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PhotoReviewAccess(db, _time)
            .AuthorizeAsync(actor, PhotoReviewCapability.Evidence, Ct));
    }

    private async Task<PhotoReviewActor> ReadyActorAsync(string role = AccountRoles.Moderator)
    {
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var now = _time.GetUtcNow();
        var keyHash = System.Security.Cryptography.SHA256.HashData(session.ToByteArray());
        var ticket = new byte[] { 1 };
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.users(id,status,created_at,email_confirmed,phone_number_confirmed,
              two_factor_enabled,lockout_enabled,access_failed_count,security_stamp)
            values({user},'Active',{now},true,false,true,true,0,'synthetic-review-stamp')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.user_roles(user_id,role_id) select {user},id from identity.roles where name={role}
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.login_sessions(id,user_id,key_hash,ticket,security_stamp,description,policy,
              created_at,last_seen_at,idle_expires_at,absolute_expires_at,reauthenticated_at,mfa_verified_at,totp_verified_at)
            values({session},{user},{keyHash},{ticket},'synthetic-review-stamp','synthetic','Administrative',
              {now.AddMinutes(-1)},{now},{now.AddMinutes(10)},{now.AddHours(1)},{now},{now},{now})
            """, Ct);
        await transaction.CommitAsync(Ct);
        return new PhotoReviewActor(user, session);
    }

    private Task InvalidateAsync(PhotoReviewActor actor, string condition) => condition switch
    {
        "restricted" => ExecuteAsync("update identity.users set status='Restricted' where id=$1", actor.UserId),
        "unconfirmed" => ExecuteAsync("update identity.users set email_confirmed=false where id=$1", actor.UserId),
        "mfa-disabled" => ExecuteAsync("update identity.users set two_factor_enabled=false where id=$1", actor.UserId),
        "temporary-password" => ExecuteAsync("update identity.users set temporary_password_expires_at=$2 where id=$1", actor.UserId, _time.GetUtcNow().AddHours(1)),
        "role-removed" => ExecuteAsync("delete from identity.user_roles where user_id=$1", actor.UserId),
        "stamp-changed" => ExecuteAsync("update identity.users set security_stamp='changed' where id=$1", actor.UserId),
        "revoked" => ExecuteAsync("update identity.login_sessions set revoked_at=$2,revocation_reason='test' where id=$1", actor.SessionId, _time.GetUtcNow()),
        "idle-expired" => ExecuteAsync("update identity.login_sessions set idle_expires_at=$2 where id=$1", actor.SessionId, _time.GetUtcNow()),
        "absolute-expired" => ExecuteAsync("update identity.login_sessions set idle_expires_at=$2,absolute_expires_at=$2 where id=$1", actor.SessionId, _time.GetUtcNow()),
        "no-mfa-proof" => ExecuteAsync("update identity.login_sessions set mfa_verified_at=null,totp_verified_at=null where id=$1", actor.SessionId),
        "future-mfa-proof" => ExecuteAsync("update identity.login_sessions set mfa_verified_at=$2 where id=$1", actor.SessionId, _time.GetUtcNow().AddMinutes(1)),
        "normal-policy" => ExecuteAsync("update identity.login_sessions set policy='Normal' where id=$1", actor.SessionId),
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    private async Task<bool> AuthorizedAsync(PhotoReviewActor actor, PhotoReviewCapability capability)
    {
        await using var db = postgres.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        return await new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, capability, Ct);
    }

    private async Task<bool> ChangeAsync(Guid userId, PhotoReviewCapability capability, bool enabled)
    {
        await using var db = postgres.CreateDbContext();
        Assert.True(OperatorName.TryParse("review-test", out var operatorName));
        return await new PhotoReviewAccess(db, _time).SetCapabilityAsync(userId, capability, enabled, operatorName, Ct);
    }

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class RefuseGrantAudit : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal))
            {
                throw new IOException("Synthetic audit refusal");
            }
            return ValueTask.FromResult(result);
        }
    }
}
