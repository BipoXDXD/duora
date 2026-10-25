using System.Data.Common;
using System.Transactions;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task OwnerAppealRequestExtendsRetentionAndCommitsTheProtocolVersionsReceiptAndAuditTogether()
    {
        var data = await RejectedForAppealAsync();
        var history = await AppealDecisionSnapshotAsync();
        var files = Files();

        var result = Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));

        Assert.Equal(data.Item.UploadId, result.PhotoId);
        Assert.NotEqual(Guid.Empty, result.AppealId);
        Assert.Equal(data.Rejection.PhotoVersion + 1, result.PhotoVersion);
        Assert.Equal(_time.GetUtcNow(), result.RequestedAt);
        Assert.Equal(result.RequestedAt.AddDays(7), result.ResponseTargetAt);
        Assert.Equal(data.RejectedAt.AddDays(30), result.MaximumRetentionUntil);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_appeals a
            join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id
            join media.review_candidates c on c.generation=a.candidate_generation
            where a.id=$1 and a.original_decision_id=$2 and a.owner_id=u.owner_id and a.owner_id=$3
              and a.status='Open' and a.version=1 and a.requested_at=$4 and a.closed_at is null
              and a.response_target_at=$5 and a.maximum_retention_until=$6
              and a.candidate_generation=r.candidate_generation and a.policy_version=r.policy_version
              and c.case_id=r.id and c.upload_id=u.id and c.retention_until=a.maximum_retention_until
              and c.cleanup_requested_at is null and c.cleanup_token is null and c.deleted_at is null
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED' and u.version=$7
              and r.version=$8
            """, result.AppealId, data.DecisionId, data.Owner.UserId, result.RequestedAt,
            result.ResponseTargetAt, result.MaximumRetentionUntil, result.PhotoVersion, data.Rejection.Version + 1));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_appeal_commands where actor_id=$1 and appeal_id=$2
              and result_photo_version=$3
            """, data.Owner.UserId, result.AppealId, result.PhotoVersion));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from ops.audit_events where actor_kind='User' and actor_id=$1
              and action='safety.photo_review_appeal_requested' and target_type='photo_review_appeal'
              and target_id=$2 and target_version=1
            """, data.Owner.UserId, result.AppealId));
        Assert.Equal(history, await AppealDecisionSnapshotAsync());
        Assert.Equal(files, Files());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Item.UploadId));
    }

    [Fact]
    public async Task OwnerAppealReplayUsesItsConfirmedReceiptBeforeVersionDeadlineOrStorage()
    {
        var data = await RejectedForAppealAsync();
        var key = Key();
        var result = Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));
        _time.Advance(TimeSpan.FromDays(8));
        await RefreshAppealOwnerSessionAsync(data.Owner);
        var files = Files();
        var snapshot = await AppealProjectionAsync();

        Assert.Equal(result, Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests(
            new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true)).RequestAsync(
                data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct)));

        Assert.Equal(files, Files());
        Assert.Equal(snapshot, await AppealProjectionAsync());
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where original_decision_id=$1", data.DecisionId));
    }

    [Fact]
    public async Task OwnerAppealRejectsAReusedKeyForAnotherPhoto()
    {
        var data = await RejectedForAppealAsync();
        var key = Key();
        Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));
        var before = await AppealProjectionAsync();

        Assert.IsType<PhotoReviewAppealRequestResult.KeyReused>(await AppealRequests().RequestAsync(
            data.Owner, Guid.NewGuid(), data.Rejection.PhotoVersion, key, Ct));

        Assert.Equal(before, await AppealProjectionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerAppealEnforcesTheSevenDayBoundary(bool exactlyAtDeadline)
    {
        var data = await RejectedForAppealAsync();
        _time.Advance(TimeSpan.FromDays(7).Subtract(exactlyAtDeadline ? TimeSpan.Zero : TimeSpan.FromTicks(10)));
        await RefreshAppealOwnerSessionAsync(data.Owner);
        var before = await AppealProjectionAsync();
        var files = Files();

        var result = await AppealRequests().RequestAsync(data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct);

        if (exactlyAtDeadline)
        {
            Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(result);
            Assert.Equal(before, await AppealProjectionAsync());
        }
        else
        {
            Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(result);
        }

        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("stamp")]
    [InlineData("privileged")]
    public async Task OwnerAppealRequiresALiveNormalSessionEvenForConfirmedReplay(string failure)
    {
        var data = await RejectedForAppealAsync();
        var key = Key();
        Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));
        if (failure == "revoked")
        {
            await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", data.Owner.SessionId, _time.GetUtcNow());
        }
        else if (failure == "expired")
        {
            await ExecuteAsync("update identity.login_sessions set idle_expires_at=$2 where id=$1", data.Owner.SessionId, _time.GetUtcNow());
        }
        else if (failure == "stamp")
        {
            await ExecuteAsync("update identity.users set security_stamp='changed-stamp' where id=$1", data.Owner.UserId);
        }
        else
        {
            // D13 forbids participant and privileged roles on the same account.
            await ExecuteAsync("delete from identity.user_roles where user_id=$1", data.Owner.UserId);
            await ExecuteAsync("insert into identity.user_roles(user_id,role_id) select $1,id from identity.roles where name='moderator'", data.Owner.UserId);
        }

        var before = await AppealProjectionAsync();
        var files = Files();
        Assert.IsType<PhotoReviewAppealRequestResult.Forbidden>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));
        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OwnerAppealCannotUseAnApprovedPhoto()
    {
        var (reviewer, approvedItem) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, approvedItem, Key()));
        var owner = await AppealOwnerAsync(approvedItem);
        var before = await AppealProjectionAsync();
        var files = Files();

        Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(await AppealRequests().RequestAsync(
            owner, approvedItem.UploadId, approved.PhotoVersion, Key(), Ct));

        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("unavailable")]
    [InlineData("technical")]
    [InlineData("waiting")]
    [InlineData("missing-human-proof")]
    public async Task OwnerAppealRequiresAnActualHumanRejectionFact(string outcome)
    {
        var item = await CaseAsync();
        var owner = await AppealOwnerAsync(item);
        if (outcome == "expired")
        {
            _time.Advance(TimeSpan.FromHours(73));
            Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
            await RefreshAppealOwnerSessionAsync(owner);
        }
        else if (outcome != "waiting")
        {
            // State fixtures for outcomes whose producer belongs to another slice; no human fact is fabricated.
            var code = outcome switch
            {
                "unavailable" => "PHOTO_REVIEW_UNAVAILABLE",
                "technical" => "PROCESSING_LIMIT",
                _ => "PHOTO_REVIEW_REJECTED",
            };
            await ExecuteAsync("update media.uploads set state='Rejected',rejection_code=$2,version=version+1 where id=$1", item.UploadId, code);
        }

        var before = await AppealProjectionAsync();
        var files = Files();
        var version = await ScalarAsync("select version from media.uploads where id=$1", item.UploadId);

        Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(await AppealRequests().RequestAsync(
            owner, item.UploadId, version, Key(), Ct));

        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerAppealCannotExtendADivergentCandidateSnapshot(bool generationChanged)
    {
        var data = await RejectedForAppealAsync();
        if (generationChanged)
        {
            await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", data.Item.CaseId, Guid.NewGuid());
        }
        else
        {
            await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 second' where generation=$1", data.Generation);
        }

        var before = await AppealProjectionAsync();
        var files = Files();
        Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OwnerAppealGivesTheSameResultForAnotherOwnersPhotoAndAMissingPhoto()
    {
        var data = await RejectedForAppealAsync();
        var stranger = await ActorAsync(Duora.Core.Identity.AccountRoles.Participant);
        var before = await AppealProjectionAsync();
        Assert.IsType<PhotoReviewAppealRequestResult.NotFound>(await AppealRequests().RequestAsync(
            stranger, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
        Assert.IsType<PhotoReviewAppealRequestResult.NotFound>(await AppealRequests().RequestAsync(
            stranger, Guid.NewGuid(), data.Rejection.PhotoVersion, Key(), Ct));
        Assert.Equal(before, await AppealProjectionAsync());
    }

    [Fact]
    public async Task OwnerAppealRejectsAStalePhotoVersionWithoutExtendingRetention()
    {
        var data = await RejectedForAppealAsync();
        var before = await AppealProjectionAsync();
        Assert.IsType<PhotoReviewAppealRequestResult.PreconditionFailed>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion - 1, Key(), Ct));
        Assert.Equal(before, await AppealProjectionAsync());
    }

    [Fact]
    public async Task ConcurrentOwnerAppealRequestsCommitOnlyOneProtocolAndExtension()
    {
        var data = await RejectedForAppealAsync();
        var results = await Task.WhenAll(
            AppealRequests().RequestAsync(data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct),
            AppealRequests().RequestAsync(data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));

        Assert.Single(results.OfType<PhotoReviewAppealRequestResult.Accepted>());
        Assert.Single(results.Where(result => result is PhotoReviewAppealRequestResult.PreconditionFailed or PhotoReviewAppealRequestResult.StateConflict));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where original_decision_id=$1", data.DecisionId));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_appeal_requested'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerAppealCannotUseMissingOrCorruptedEvidence(bool corrupt)
    {
        var data = await RejectedForAppealAsync();
        var store = new FileSystemObjectStore(_root.FullName);
        var key = ObjectKey.ForReviewCandidate(data.Generation, PhotoVariant.Card);
        byte[]? corrupted = null;
        if (corrupt)
        {
            await using var original = await store.OpenReadAsync(key, Ct);
            Assert.NotNull(original);
            using var buffer = new MemoryStream();
            await original.CopyToAsync(buffer, Ct);
            corrupted = buffer.ToArray();
            Assert.NotEmpty(corrupted);
            corrupted[0] ^= 1;
        }

        await store.DeleteAsync(key, Ct);
        if (corrupted is not null)
        {
            using var content = new MemoryStream(corrupted);
            await store.PutAsync(key, content, corrupted.Length, "image/webp", Ct);
        }

        var before = await AppealProjectionAsync();
        var files = Files();
        Assert.IsType<PhotoReviewAppealRequestResult.Unavailable>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OwnerAppealRollsBackTheRetentionVersionsAndProtocolWhenAuditFails()
    {
        var data = await RejectedForAppealAsync();
        var before = await AppealProjectionAsync();
        var files = Files();
        var refuse = new RefuseAppealRequestAudit();

        await Assert.ThrowsAsync<IOException>(() => AppealRequests(interceptors: [refuse]).RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));

        Assert.True(refuse.Fired);
        Assert.Equal(before, await AppealProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_appeals"));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_appeal_commands"));
        Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
    }

    [Fact]
    public async Task OwnerAppealCannotKeepStaleAuthorizationOrDeadlineAfterWaitingForTheOwnersGuard()
    {
        var data = await RejectedForAppealAsync();
        string? winner = null;
        var result = await AppealWhileGuardedAsync(data, async (connection, transaction) =>
        {
            _time.Advance(TimeSpan.FromDays(7));
            await using var refresh = new NpgsqlCommand("""
                update identity.login_sessions set last_seen_at=$2,idle_expires_at=$3,absolute_expires_at=$4 where id=$1
                """, connection, transaction);
            refresh.Parameters.AddWithValue(data.Owner.SessionId);
            refresh.Parameters.AddWithValue(_time.GetUtcNow());
            refresh.Parameters.AddWithValue(_time.GetUtcNow().AddMinutes(10));
            refresh.Parameters.AddWithValue(_time.GetUtcNow().AddHours(1));
            Assert.Equal(1, await refresh.ExecuteNonQueryAsync(Ct));
            winner = await ReadAppealProjectionAsync(connection, transaction, Ct);
        });

        Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(result);
        Assert.Equal(winner, await AppealProjectionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerAppealRevalidatesDeletionOrSessionRevocationAfterARealGuardWait(bool revoked)
    {
        var data = await RejectedForAppealAsync();
        string? winner = null;
        var files = Files();
        var result = await AppealWhileGuardedAsync(data, async (connection, transaction) =>
        {
            await using var change = new NpgsqlCommand(revoked
                ? "update identity.login_sessions set revoked_at=$2 where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1", connection, transaction);
            change.Parameters.AddWithValue(revoked ? data.Owner.SessionId : data.Item.UploadId);
            if (revoked)
            {
                change.Parameters.AddWithValue(_time.GetUtcNow());
            }

            Assert.Equal(1, await change.ExecuteNonQueryAsync(Ct));
            winner = await ReadAppealProjectionAsync(connection, transaction, Ct);
        });

        if (revoked)
        {
            Assert.IsType<PhotoReviewAppealRequestResult.Forbidden>(result);
        }
        else
        {
            Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(result);
        }

        Assert.Equal(winner, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OwnerAppealVerifiesEvidenceWithoutHoldingBusinessSqlLocks()
    {
        var data = await RejectedForAppealAsync();
        var reads = 0;
        var store = new AppealEvidenceStore(new FileSystemObjectStore(_root.FullName), async (_, ct) =>
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var (sql, id) in new[]
            {
                ("select id from media.uploads where id=$1 for update nowait", data.Item.UploadId),
                ("select id from safety.photo_review_cases where id=$1 for update nowait", data.Item.CaseId),
                ("select generation from media.review_candidates where generation=$1 for update nowait", data.Generation),
            })
            {
                await using var probe = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 2 };
                probe.Parameters.AddWithValue(id);
                Assert.Equal(id, await probe.ExecuteScalarAsync(ct));
            }

            await transaction.RollbackAsync(ct);
            reads++;
        });

        Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests(store).RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct));
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("deleted")]
    [InlineData("revoked")]
    public async Task OwnerAppealRevalidatesTheWinnerAfterItsStorageRead(string winnerKind)
    {
        var data = await RejectedForAppealAsync();
        var fired = false;
        string? winner = null;
        var files = Files();
        var store = new AppealEvidenceStore(new FileSystemObjectStore(_root.FullName), async (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            if (winnerKind == "deadline")
            {
                _time.Advance(TimeSpan.FromDays(7));
                await RefreshAppealOwnerSessionAsync(data.Owner);
            }
            else if (winnerKind == "deleted")
            {
                await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", data.Item.UploadId);
            }
            else
            {
                await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", data.Owner.SessionId, _time.GetUtcNow());
            }

            winner = await AppealProjectionAsync();
        });

        var result = await AppealRequests(store).RequestAsync(data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), Ct);

        Assert.True(fired);
        if (winnerKind == "revoked")
        {
            Assert.IsType<PhotoReviewAppealRequestResult.Forbidden>(result);
        }
        else
        {
            Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(result);
        }

        Assert.Equal(winner, await AppealProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerAppealDoesNotConfirmAnUncertainCommitAndRecoversWithTheSameKey(bool afterCommit)
    {
        var data = await RejectedForAppealAsync();
        var key = Key();
        var before = await AppealProjectionAsync();
        var files = Files();
        var fault = new AppealRequestCommitFault(_connectionString, data.DecisionId, afterCommit);

        await Assert.ThrowsAsync<IOException>(() => AppealRequests(interceptors: [fault]).RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));

        Assert.True(fault.Fired);
        if (!afterCommit)
        {
            Assert.Equal(before, await AppealProjectionAsync());
        }

        var result = Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct));
        Assert.Equal(result, Assert.IsType<PhotoReviewAppealRequestResult.Accepted>(await AppealRequests().RequestAsync(
            data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, key, Ct)));
        Assert.Equal(data.Rejection.PhotoVersion + 1, result.PhotoVersion);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where original_decision_id=$1", data.DecisionId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_commands where appeal_id=$1", result.AppealId));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_appeal_requested' and target_id=$1", result.AppealId));
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task OwnerAppealCannotJoinAnAmbientTransaction()
    {
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppealRequests().RequestAsync(
            default, Guid.NewGuid(), 1, Key(), Ct));
    }

    private async Task<RejectedAppealData> RejectedForAppealAsync()
    {
        var (reviewer, item) = await PreparedAsync();
        var rejection = Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
            new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        await using var db = Context();
        var decisionId = await db.Database.SqlQuery<Guid>($"""
            select id as "Value" from safety.photo_review_decisions where case_id={item.CaseId} and decision='reject'
            """).SingleAsync(Ct);
        return new RejectedAppealData(await AppealOwnerAsync(item), item, rejection,
            decisionId, await CleanupGenerationAsync(item), _time.GetUtcNow());
    }

    private async Task<PhotoReviewActor> AppealOwnerAsync(CaseData item)
    {
        await using var db = Context();
        var sessionId = await db.Database.SqlQuery<Guid>($"""
            select id as "Value" from identity.login_sessions where user_id={item.OwnerId}
            """).SingleAsync(Ct);
        return new PhotoReviewActor(item.OwnerId, sessionId);
    }

    private Task RefreshAppealOwnerSessionAsync(PhotoReviewActor owner) => ExecuteAsync("""
        update identity.login_sessions set last_seen_at=$2,idle_expires_at=$3,absolute_expires_at=$4 where id=$1
        """, owner.SessionId, _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(10), _time.GetUtcNow().AddHours(1));

    private async Task<PhotoReviewAppealRequestResult> AppealWhileGuardedAsync(RejectedAppealData data,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> beforeRelease)
    {
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select user_id from safety.user_guards where user_id=$1 for update", locker, held);
        hold.Parameters.AddWithValue(data.Owner.UserId);
        Assert.Equal(data.Owner.UserId, await hold.ExecuteScalarAsync(Ct));
        var label = "appeal-owner-guard-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label };
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(settings.ConnectionString);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new PhotoReviewAppeals(new ContextFactory(options.Options), new FileSystemObjectStore(_root.FullName), _time)
            .RequestAsync(data.Owner, data.Item.UploadId, data.Rejection.PhotoVersion, Key(), attempt.Token);
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

            Assert.True(waiting, "Appeal request must wait on the owner's actual Safety guard.");
            await beforeRelease(locker, held);
            await held.CommitAsync(Ct);
            released = true;
            return await request.WaitAsync(TimeSpan.FromSeconds(5), Ct);
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

    private PhotoReviewAppeals AppealRequests(IObjectStore? store = null, params IInterceptor[] interceptors) =>
        new(Factory(interceptors), store ?? new FileSystemObjectStore(_root.FullName), _time);

    private async Task<string> AppealProjectionAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        return await ReadAppealProjectionAsync(connection, null, Ct);
    }

    private static async Task<string> ReadAppealProjectionAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var existing = await ReadExpirationProjectionAsync(connection, transaction, cancellationToken);
        // Before the additive migration exists, RED still reaches behavior assertions rather than a missing-table error.
        await using var catalog = new NpgsqlCommand("select to_regclass('safety.photo_review_appeals') is not null", connection, transaction);
        if (await catalog.ExecuteScalarAsync(cancellationToken) is not true)
        {
            return existing + "\n[]";
        }

        await using var records = new NpgsqlCommand("""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(a) order by a.id),'[]'::jsonb) from safety.photo_review_appeals a),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.actor_id,c.key_hash),'[]'::jsonb) from safety.photo_review_appeal_commands c)
            )::text
            """, connection, transaction);
        return existing + "\n" + Assert.IsType<string>(await records.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<string> AppealDecisionSnapshotAsync()
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(d) order by d.id),'[]'::jsonb) from safety.photo_review_decisions d),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.decision_id,v.variant),'[]'::jsonb) from safety.photo_review_decision_variants v),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.actor_id,c.operation,c.key_hash),'[]'::jsonb) from safety.photo_review_decision_commands c)
            )::text as "Value"
            """).SingleAsync(Ct);
    }

    private sealed record RejectedAppealData(PhotoReviewActor Owner, CaseData Item,
        PhotoReviewRejectionResult.Rejected Rejection, Guid DecisionId, Guid Generation, DateTimeOffset RejectedAt);

    private sealed class AppealEvidenceStore(IObjectStore inner, Func<ObjectKey, CancellationToken, Task> beforeOpen) : IObjectStore
    {
        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            await beforeOpen(key, cancellationToken);
            return await inner.OpenReadAsync(key, cancellationToken);
        }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An appeal request only verifies existing private evidence.");

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An appeal request must not delete evidence.");
    }

    private sealed class AppealRequestCommitFault(string connectionString, Guid decisionId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasAppealAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before appeal request commit.");
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
            if (await HasAppealAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost appeal request commit response.");
            }
        }

        private async Task<bool> HasAppealAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from safety.photo_review_appeals where original_decision_id=$1)", connection, transaction);
            command.Parameters.AddWithValue(decisionId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private sealed class RefuseAppealRequestAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_appeal_requested"))
            {
                Fired = true;
                throw new IOException("Synthetic owner appeal audit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
