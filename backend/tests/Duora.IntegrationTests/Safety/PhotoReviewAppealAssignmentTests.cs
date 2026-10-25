using System.Data.Common;
using System.Security.Cryptography;
using System.Transactions;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task AppealAssignmentChangesOnlyTheRootAndItsOwnAuditedHistory()
    {
        var data = await AssignableAppealAsync();
        var history = await AppealDecisionSnapshotAsync();
        var files = Files();

        var result = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, Key()));

        Assert.Equal(data.Appeal.AppealId, result.AppealId);
        Assert.Equal(2, result.Version);
        await AssertAppealAssignmentAsync(data, result.Version, data.Reviewer.UserId);
        Assert.Equal(history, await AppealDecisionSnapshotAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OriginalHumanRejectorCannotBecomeTheAppealReviewerEvenWithBothCapabilities()
    {
        var data = await AssignableAppealAsync();
        await GrantAsync(data.Original.UserId, PhotoReviewCapability.Appeal, true);
        var before = await AppealAssignmentProjectionAsync();
        var files = Files();

        Assert.IsType<PhotoReviewAppealAssignmentResult.ReviewerUnavailable>(await AssignAppealAsync(
            data with { Reviewer = data.Original }, Key()));

        Assert.Equal(before, await AppealAssignmentProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OriginalRejectorWithAssignMayRouteTheAppealToADifferentReviewer()
    {
        var data = await AssignableAppealAsync();
        await GrantAsync(data.Original.UserId, PhotoReviewCapability.Assign, true);
        data = data with { Assigner = data.Original };

        var result = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, Key()));

        await AssertAppealAssignmentAsync(data, result.Version, data.Reviewer.UserId);
        Assert.NotEqual(data.Original.UserId, data.Reviewer.UserId);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("stamp")]
    [InlineData("normal-policy")]
    [InlineData("no-mfa")]
    public async Task AppealAssignmentRequiresLiveAdministrativeAssignAuthority(string failure)
    {
        var data = await AssignableAppealAsync();
        if (failure == "capability")
        {
            await GrantAsync(data.Assigner.UserId, PhotoReviewCapability.Assign, false);
        }
        else if (failure == "stamp")
        {
            await ExecuteAsync("update identity.users set security_stamp='changed' where id=$1", data.Assigner.UserId);
        }
        else
        {
            var sql = failure switch
            {
                "revoked" => "update identity.login_sessions set revoked_at=$2 where id=$1",
                "expired" => "update identity.login_sessions set idle_expires_at=$2 where id=$1",
                "normal-policy" => "update identity.login_sessions set policy='Normal' where id=$1",
                _ => "update identity.login_sessions set mfa_verified_at=null,totp_verified_at=null where id=$1",
            };
            if (failure is "revoked" or "expired")
            {
                await ExecuteAsync(sql, data.Assigner.SessionId, _time.GetUtcNow());
            }
            else
            {
                await ExecuteAsync(sql, data.Assigner.SessionId);
            }
        }

        var before = await AppealAssignmentProjectionAsync();
        Assert.IsType<PhotoReviewAppealAssignmentResult.Forbidden>(await AssignAppealAsync(data, Key()));
        Assert.IsType<PhotoReviewAppealAssignmentResult.Forbidden>(await AppealAssignments().AssignAsync(
            data.Assigner, Guid.NewGuid(), data.Reviewer.UserId, 1, Key(), Ct));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("no-evidence")]
    [InlineData("no-appeal")]
    [InlineData("no-mfa")]
    [InlineData("temporary")]
    [InlineData("restricted")]
    [InlineData("suspended")]
    [InlineData("deleting")]
    public async Task AppealReviewerReadinessNeedsEvidenceAndAppealNotJustInitialDecide(string failure)
    {
        var data = await AssignableAppealAsync();
        await GrantAsync(data.Reviewer.UserId, PhotoReviewCapability.Decide, true);
        if (failure == "participant")
        {
            data = data with { Reviewer = await ActorAsync(AccountRoles.Participant) };
        }
        else if (failure is "no-evidence" or "no-appeal")
        {
            await GrantAsync(data.Reviewer.UserId,
                failure == "no-evidence" ? PhotoReviewCapability.Evidence : PhotoReviewCapability.Appeal, false);
        }
        else
        {
            var sql = failure switch
            {
                "no-mfa" => "update identity.users set two_factor_enabled=false where id=$1",
                "temporary" => "update identity.users set temporary_password_expires_at=$2 where id=$1",
                "restricted" => "update identity.users set status='Restricted' where id=$1",
                "suspended" => "update identity.users set status='Suspended' where id=$1",
                _ => "update identity.users set status='DeletionPending' where id=$1",
            };
            if (failure == "temporary")
            {
                await ExecuteAsync(sql, data.Reviewer.UserId, _time.GetUtcNow().AddHours(1));
            }
            else
            {
                await ExecuteAsync(sql, data.Reviewer.UserId);
            }
        }

        var before = await AppealAssignmentProjectionAsync();
        var files = Files();
        Assert.IsType<PhotoReviewAppealAssignmentResult.ReviewerUnavailable>(await AssignAppealAsync(data, Key()));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task AppealAssignmentRejectsAStaleRootVersionAndAMissingRoot()
    {
        var data = await AssignableAppealAsync();
        var before = await AppealAssignmentProjectionAsync();
        Assert.IsType<PhotoReviewAppealAssignmentResult.PreconditionFailed>(await AssignAppealAsync(data, Key(), version: 0));
        Assert.IsType<PhotoReviewAppealAssignmentResult.NotFound>(await AppealAssignments().AssignAsync(
            data.Assigner, Guid.NewGuid(), data.Reviewer.UserId, 1, Key(), Ct));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
    }

    [Fact]
    public async Task AppealAssignmentReplayDoesNotUndoANewerConfirmedAssignment()
    {
        var data = await AssignableAppealAsync();
        var key = Key();
        var first = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key));
        var nextReviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Appeal);
        var next = data with { Reviewer = nextReviewer };
        var second = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(next, Key(), first.Version));
        Assert.Equal(3, second.Version);
        var before = await AppealAssignmentProjectionAsync();

        Assert.Equal(first, Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key)));

        Assert.Equal(before, await AppealAssignmentProjectionAsync());
        await AssertAppealAssignmentAsync(next, second.Version, nextReviewer.UserId);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("revoked")]
    [InlineData("stamp")]
    public async Task AppealAssignmentReplayStillRequiresLiveAssignAuthority(string failure)
    {
        var data = await AssignableAppealAsync();
        var key = Key();
        Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key));
        if (failure == "capability")
        {
            await GrantAsync(data.Assigner.UserId, PhotoReviewCapability.Assign, false);
        }
        else if (failure == "revoked")
        {
            await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", data.Assigner.SessionId, _time.GetUtcNow());
        }
        else
        {
            await ExecuteAsync("update identity.users set security_stamp='changed' where id=$1", data.Assigner.UserId);
        }

        var before = await AppealAssignmentProjectionAsync();
        Assert.IsType<PhotoReviewAppealAssignmentResult.Forbidden>(await AssignAppealAsync(data, key));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
    }

    [Fact]
    public async Task AppealAssignmentCannotReuseAConfirmedKeyForAnotherReviewer()
    {
        var data = await AssignableAppealAsync();
        var key = Key();
        Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key));
        var other = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Appeal);
        var before = await AppealAssignmentProjectionAsync();
        Assert.IsType<PhotoReviewAppealAssignmentResult.KeyReused>(await AssignAppealAsync(data with { Reviewer = other }, key));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
    }

    [Fact]
    public async Task ConcurrentAppealAssignmentsAdvanceTheRootOnlyOnce()
    {
        var data = await AssignableAppealAsync();
        var results = await Task.WhenAll(AssignAppealAsync(data, Key()), AssignAppealAsync(data, Key()));
        Assert.Single(results.OfType<PhotoReviewAppealAssignmentResult.Assigned>());
        Assert.Single(results, result => result is PhotoReviewAppealAssignmentResult.PreconditionFailed or PhotoReviewAppealAssignmentResult.StateConflict);
        await AssertAppealAssignmentAsync(data, 2, data.Reviewer.UserId);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_assignments where appeal_id=$1", data.Appeal.AppealId));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("generation")]
    [InlineData("retention")]
    [InlineData("policy")]
    [InlineData("root-deadline")]
    public async Task AppealAssignmentRevalidatesTheExactOpenCandidateContext(string drift)
    {
        var data = await AssignableAppealAsync();
        if (drift == "deleted")
        {
            await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", data.Data.Item.UploadId);
        }
        else if (drift == "generation")
        {
            await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", data.Data.Item.CaseId, Guid.NewGuid());
        }
        else if (drift == "retention")
        {
            await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 second' where generation=$1", data.Data.Generation);
        }
        else if (drift == "policy")
        {
            await ExecuteAsync("update safety.photo_review_cases set policy_version='unknown-policy' where id=$1", data.Data.Item.CaseId);
        }
        else
        {
            // Internally consistent mutable snapshots still cannot replace the immutable rejection time.
            await ExecuteAsync("""
                update safety.photo_review_appeals set original_rejected_at=original_rejected_at+interval '1 hour',
                  requested_at=requested_at+interval '1 hour',response_target_at=response_target_at+interval '1 hour',
                  maximum_retention_until=maximum_retention_until+interval '1 hour' where id=$1
                """, data.Appeal.AppealId);
            await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 hour' where generation=$1", data.Data.Generation);
            _time.Advance(TimeSpan.FromHours(2));
            data = data with { Assigner = await FreshAppealAssignerSessionAsync(data.Assigner.UserId) };
        }

        var before = await AppealAssignmentProjectionAsync();
        var files = Files();
        Assert.IsType<PhotoReviewAppealAssignmentResult.StateConflict>(await AssignAppealAsync(data, Key()));
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealAssignmentUsesTheThirtyDayMaximumInsteadOfTheExpiredInitialReviewDeadline(bool exactlyAtMaximum)
    {
        var data = await AssignableAppealAsync();
        _time.Advance(TimeSpan.FromDays(30).Subtract(exactlyAtMaximum ? TimeSpan.Zero : TimeSpan.FromTicks(10)));
        data = data with { Assigner = await FreshAppealAssignerSessionAsync(data.Assigner.UserId) };
        var before = await AppealAssignmentProjectionAsync();
        var result = await AssignAppealAsync(data, Key());
        if (exactlyAtMaximum)
        {
            Assert.IsType<PhotoReviewAppealAssignmentResult.StateConflict>(result);
            Assert.Equal(before, await AppealAssignmentProjectionAsync());
        }
        else
        {
            Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(result);
        }
    }

    [Fact]
    public async Task AppealAssignmentAuditFailureRollsBackTheRootReceiptAndHistory()
    {
        var data = await AssignableAppealAsync();
        var before = await AppealAssignmentProjectionAsync();
        var fault = new RefuseAppealAssignmentAudit();
        await Assert.ThrowsAsync<IOException>(() => AppealAssignments(fault).AssignAsync(
            data.Assigner, data.Appeal.AppealId, data.Reviewer.UserId, 1, Key(), Ct));
        Assert.True(fault.Fired);
        Assert.Equal(before, await AppealAssignmentProjectionAsync());
        Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, Key()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealAssignmentUnknownCommitIsNotConfirmedAndSameKeyRecoversOneFact(bool afterCommit)
    {
        var data = await AssignableAppealAsync();
        var key = Key();
        var before = await AppealAssignmentProjectionAsync();
        var fault = new AppealAssignmentCommitFault(_connectionString, data.Appeal.AppealId, afterCommit);
        await Assert.ThrowsAsync<IOException>(() => AppealAssignments(fault).AssignAsync(
            data.Assigner, data.Appeal.AppealId, data.Reviewer.UserId, 1, key, Ct));
        Assert.True(fault.Fired);
        if (!afterCommit)
        {
            Assert.Equal(before, await AppealAssignmentProjectionAsync());
        }

        var result = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key));
        Assert.Equal(2, result.Version);
        Assert.Equal(result, Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, key)));
        await AssertAppealAssignmentAsync(data, result.Version, data.Reviewer.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealAssignmentCannotUseStaleAuthorizationOrUploadAfterAnActualGuardWait(bool revoked)
    {
        var data = await AssignableAppealAsync();
        var guardUser = revoked ? data.Assigner.UserId : data.Data.Owner.UserId;
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select user_id from safety.user_guards where user_id=$1 for update", locker, held);
        hold.Parameters.AddWithValue(guardUser);
        Assert.Equal(guardUser, await hold.ExecuteScalarAsync(Ct));
        var label = "appeal-assignment-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label }.ConnectionString);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new PhotoReviewAppealAssignments(new ContextFactory(options.Options), _time).AssignAsync(
            data.Assigner, data.Appeal.AppealId, data.Reviewer.UserId, 1, Key(), attempt.Token);
        var released = false;
        try
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            observation.CancelAfter(TimeSpan.FromSeconds(5));
            var waiting = false;
            for (var count = 0; count < 100 && !request.IsCompleted; count++)
            {
                waiting = await ScalarAsync("select count(*) from pg_stat_activity where application_name=$1 and wait_event_type='Lock'", label) == 1;
                if (waiting)
                {
                    break;
                }

                await Task.Delay(10, observation.Token);
            }

            Assert.True(waiting, "Appeal assignment must wait on the actor or owner's actual Safety guard.");
            await using var change = new NpgsqlCommand(revoked
                ? "update identity.login_sessions set revoked_at=$2 where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1", locker, held);
            change.Parameters.AddWithValue(revoked ? data.Assigner.SessionId : data.Data.Item.UploadId);
            if (revoked)
            {
                change.Parameters.AddWithValue(_time.GetUtcNow());
            }

            Assert.Equal(1, await change.ExecuteNonQueryAsync(Ct));
            var winner = await ReadAppealAssignmentProjectionAsync(locker, held, Ct);
            await held.CommitAsync(Ct);
            released = true;
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            if (revoked)
            {
                Assert.IsType<PhotoReviewAppealAssignmentResult.Forbidden>(result);
            }
            else
            {
                Assert.IsType<PhotoReviewAppealAssignmentResult.StateConflict>(result);
            }

            Assert.Equal(winner, await AppealAssignmentProjectionAsync());
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!released)
                {
                    await held.RollbackAsync(cleanup.Token);
                }
            }
            finally
            {
                await attempt.CancelAsync();
                try
                {
                    await request;
                }
                catch (OperationCanceledException) when (request.IsCompleted && request.IsCanceled && attempt.IsCancellationRequested)
                {
                }
            }
        }
    }

    [Fact]
    public async Task AppealAssignmentCannotJoinAnAmbientTransaction()
    {
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppealAssignments().AssignAsync(
            default, Guid.NewGuid(), Guid.NewGuid(), 1, Key(), Ct));
    }

    private async Task<AssignableAppealData> AssignableAppealAsync()
    {
        var data = await RejectedForAppealAsync();
        var appeal = Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
        await using var db = Context();
        var original = await db.Database.SqlQuery<OriginalAppealActor>($"""
            select d.actor_id as "UserId",s.id as "SessionId" from safety.photo_review_decisions d
            join identity.login_sessions s on s.user_id=d.actor_id where d.id={data.DecisionId}
            """).SingleAsync(Ct);
        return new(data, appeal, await ModeratorAsync(PhotoReviewCapability.Assign),
            await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Appeal), new(original.UserId, original.SessionId));
    }

    private async Task<PhotoReviewActor> FreshAppealAssignerSessionAsync(Guid userId)
    {
        var session = Guid.NewGuid();
        var hash = SHA256.HashData(session.ToByteArray());
        var ticket = new byte[] { 1 };
        var now = _time.GetUtcNow();
        await using var db = Context();
        Assert.Equal(1, await db.Database.ExecuteSqlAsync($"""
            insert into identity.login_sessions(id,user_id,key_hash,ticket,security_stamp,description,policy,
              created_at,last_seen_at,idle_expires_at,absolute_expires_at,reauthenticated_at,mfa_verified_at,totp_verified_at)
            select {session},u.id,{hash},{ticket},u.security_stamp,'synthetic-appeal-assign','Administrative',
              {now.AddMinutes(-1)},{now},{now.AddMinutes(10)},{now.AddHours(1)},{now},{now},{now}
            from identity.users u where u.id={userId}
            """, Ct));
        return new(userId, session);
    }

    private PhotoReviewAppealAssignments AppealAssignments(params IInterceptor[] interceptors) => new(Factory(interceptors), _time);

    private Task<PhotoReviewAppealAssignmentResult> AssignAppealAsync(AssignableAppealData data, IdempotencyKey key, long version = 1) =>
        AppealAssignments().AssignAsync(data.Assigner, data.Appeal.AppealId, data.Reviewer.UserId, version, key, Ct);

    private async Task AssertAppealAssignmentAsync(AssignableAppealData data, long version, Guid reviewerId)
    {
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join media.review_candidates c on c.generation=a.candidate_generation
            join safety.photo_review_appeal_assignments s on s.appeal_id=a.id and s.appeal_version=a.version
            where a.id=$1 and a.status='Open' and a.version=$2 and a.original_decision_id=$3
              and a.maximum_retention_until=$4 and c.retention_until=a.maximum_retention_until
              and s.reviewer_id=$5 and s.assigned_by=$6 and u.version=$7 and r.version=$8
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
              and c.cleanup_requested_at is null and c.deleted_at is null
            """, data.Appeal.AppealId, version, data.Data.DecisionId, data.Appeal.MaximumRetentionUntil,
            reviewerId, data.Assigner.UserId, data.Appeal.PhotoVersion, data.Data.Rejection.Version + 1));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from ops.audit_events where actor_kind='Moderator' and actor_id=$1
              and action='safety.photo_review_appeal_assigned' and target_type='photo_review_appeal' and target_id=$2 and target_version=$3
            """, data.Assigner.UserId, data.Appeal.AppealId, version));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Data.Item.UploadId));
    }

    private async Task<string> AppealAssignmentProjectionAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        return await ReadAppealAssignmentProjectionAsync(connection, null, Ct);
    }

    private static async Task<string> ReadAppealAssignmentProjectionAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var existing = await ReadAppealProjectionAsync(connection, transaction, cancellationToken);
        await using var catalog = new NpgsqlCommand("select to_regclass('safety.photo_review_appeal_assignments') is not null", connection, transaction);
        if (await catalog.ExecuteScalarAsync(cancellationToken) is not true)
        {
            return existing + "\n[]";
        }

        await using var records = new NpgsqlCommand("""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(a) order by a.id),'[]'::jsonb) from safety.photo_review_appeal_assignments a),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.actor_id,c.key_hash),'[]'::jsonb) from safety.photo_review_appeal_assignment_commands c)
            )::text
            """, connection, transaction);
        return existing + "\n" + Assert.IsType<string>(await records.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record OriginalAppealActor(Guid UserId, Guid SessionId);
    private sealed record AssignableAppealData(RejectedAppealData Data, PhotoReviewAppealRequestResult.Accepted Appeal,
        PhotoReviewActor Assigner, PhotoReviewActor Reviewer, PhotoReviewActor Original);

    private sealed class AppealAssignmentCommitFault(string connectionString, Guid appealId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasAssignmentAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before appeal assignment commit.");
            }

            return result;
        }

        public override async Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!afterCommit || Fired)
            {
                return;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (await HasAssignmentAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost appeal assignment commit response.");
            }
        }

        private async Task<bool> HasAssignmentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from safety.photo_review_appeal_assignments where appeal_id=$1)", connection, transaction);
            command.Parameters.AddWithValue(appealId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private sealed class RefuseAppealAssignmentAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_appeal_assigned"))
            {
                Fired = true;
                throw new IOException("Synthetic appeal assignment audit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
