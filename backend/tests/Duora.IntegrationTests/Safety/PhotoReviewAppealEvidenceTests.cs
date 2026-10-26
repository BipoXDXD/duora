using System.Data.Common;
using System.Security.Cryptography;
using System.Transactions;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData(PhotoVariant.Thumb)]
    [InlineData(PhotoVariant.Card)]
    [InlineData(PhotoVariant.Full)]
    public async Task AppealEvidenceAuditsTheExactAssignmentBeforeReturningOnlyPrivateVariantBytes(PhotoVariant variant)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var history = await AppealDecisionSnapshotAsync();
        var files = Files();
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));

        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await AppealReader(store).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, variant, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_appeal_evidence_accesses a
            join safety.photo_review_appeal_assignments s on s.id=a.assignment_id
            join ops.audit_events e on e.target_id=a.id
            where a.appeal_id=$1 and a.actor_id=$2 and a.appeal_version=2
              and a.candidate_generation=$3 and a.variant=$4 and a.variant_sha256=$5
              and a.policy_version='human-review-v1' and s.appeal_id=a.appeal_id
              and s.appeal_version=a.appeal_version and s.reviewer_id=a.actor_id
              and e.action='safety.photo_review_appeal_evidence_viewed' and e.actor_kind='Moderator'
              and e.actor_id=a.actor_id and e.target_type='photo_review_access' and e.target_version=a.appeal_version
            """, data.Appeal.AppealId, data.Reviewer.UserId, data.Data.Generation, PhotoVariants.Name(variant),
            SHA256.HashData(TestImages.WebP(64, 32))));
        using var delivered = new MemoryStream();
        var buffer = new byte[8];
        int count;
        while ((count = await lease.ReadAsync(buffer, Ct)) != 0)
        {
            delivered.Write(buffer, 0, count);
        }
        Assert.Equal(TestImages.WebP(64, 32), delivered.ToArray());
        Assert.Equal(ObjectKey.ForReviewCandidate(data.Data.Generation, variant), Assert.Single(store.Opened));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_evidence_accesses where appeal_id=$1", data.Appeal.AppealId));
        await AssertAppealAssignmentAsync(data, 2, data.Reviewer.UserId);
        Assert.Equal(history, await AppealDecisionSnapshotAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppealEvidenceCannotBeReadByTheInitialRejectorOrAnotherReadyModerator(bool original)
    {
        var data = await AssignedAppealForEvidenceAsync();
        await GrantAsync(data.Original.UserId, PhotoReviewCapability.Appeal, true);
        var actor = original ? data.Original : await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Appeal);
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));
        var before = await AppealReadProjectionAsync();

        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await AppealReader(store).OpenAsync(actor, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await AppealReader(store).OpenAsync(actor, Guid.NewGuid(), PhotoVariant.Full, Ct));

        Assert.Empty(store.Opened);
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("stamp")]
    [InlineData("no-mfa")]
    [InlineData("normal-policy")]
    [InlineData("no-evidence")]
    [InlineData("no-appeal")]
    [InlineData("restricted")]
    [InlineData("temporary")]
    [InlineData("participant")]
    public async Task AppealEvidenceRequiresLiveMfaEvidenceAndAppealAuthorityBeforeStorage(string failure)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var actor = data.Reviewer;
        if (failure == "participant")
        {
            actor = await ActorAsync(AccountRoles.Participant);
        }
        else if (failure is "no-evidence" or "no-appeal")
        {
            await GrantAsync(actor.UserId, failure == "no-evidence" ? PhotoReviewCapability.Evidence : PhotoReviewCapability.Appeal, false);
        }
        else if (failure is "revoked" or "temporary")
        {
            await ExecuteAsync(failure == "revoked"
                ? "update identity.login_sessions set revoked_at=$2 where id=$1"
                : "update identity.users set temporary_password_expires_at=$2 where id=$1",
                failure == "revoked" ? actor.SessionId : actor.UserId, _time.GetUtcNow());
        }
        else
        {
            var sql = failure switch
            {
                "stamp" => "update identity.users set security_stamp='changed' where id=$1",
                "restricted" => "update identity.users set status='Restricted' where id=$1",
                "normal-policy" => "update identity.login_sessions set policy='Normal' where id=$1",
                _ => "update identity.login_sessions set mfa_verified_at=null where id=$1",
            };
            await ExecuteAsync(sql, failure is "stamp" or "restricted" ? actor.UserId : actor.SessionId);
        }
        var before = await AppealReadProjectionAsync();
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));

        Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(await AppealReader(store).OpenAsync(actor, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(await AppealReader(store).OpenAsync(actor, Guid.NewGuid(), PhotoVariant.Full, Ct));

        Assert.Empty(store.Opened);
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealEvidenceReadingDoesNotWeakenDecisionStepUpOrRequireRecentTotpItself(bool missingTotp)
    {
        var data = await AssignedAppealForEvidenceAsync();
        if (missingTotp)
        {
            await ExecuteAsync("update identity.login_sessions set totp_verified_at=null where id=$1", data.Reviewer.SessionId);
        }
        else
        {
            _time.Advance(TimeSpan.FromMinutes(6));
        }

        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await AppealReader().OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        await using var proof = Context();
        await using var transaction = await proof.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        Assert.False(await new PhotoReviewAccess(proof, _time).AuthorizeAsync(data.Reviewer, PhotoReviewCapability.Appeal, Ct));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("generation")]
    [InlineData("policy")]
    [InlineData("retention")]
    public async Task AppealEvidenceRejectsDivergentRootCandidateContextWithoutOpeningStorage(string drift)
    {
        var data = await AssignedAppealForEvidenceAsync();
        if (drift == "deleted")
        {
            await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", data.Data.Item.UploadId);
        }
        else if (drift == "generation")
        {
            await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", data.Data.Item.CaseId, Guid.NewGuid());
        }
        else if (drift == "policy")
        {
            await ExecuteAsync("update safety.photo_review_cases set policy_version='unknown-policy' where id=$1", data.Data.Item.CaseId);
        }
        else
        {
            await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 second' where generation=$1", data.Data.Generation);
        }
        var before = await AppealReadProjectionAsync();
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));

        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await AppealReader(store).OpenAsync(data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));

        Assert.Empty(store.Opened);
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("extra")]
    public async Task AppealEvidenceNeverFallsBackWhenTheRequestedPrivateBytesAreMissingOrWrong(string failure)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var store = new FileSystemObjectStore(_root.FullName);
        var key = ObjectKey.ForReviewCandidate(data.Data.Generation, PhotoVariant.Card);
        await store.DeleteAsync(key, Ct);
        if (failure != "missing")
        {
            var bytes = failure == "corrupt" ? RandomNumberGenerator.GetBytes(TestImages.WebP(64, 32).Length)
                : TestImages.WebP(64, 32).Append((byte)0).ToArray();
            using var content = new MemoryStream(bytes);
            await store.PutAsync(key, content, bytes.Length, "image/webp", Ct);
        }
        var before = await AppealReadProjectionAsync();
        var files = Files();
        var traced = new AppealReadStore(store);

        Assert.IsType<PhotoReviewEvidenceResult.Unavailable>(await AppealReader(traced).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Card, Ct));

        Assert.Equal(key, Assert.Single(traced.Opened));
        Assert.Equal(before, await AppealReadProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task AppealEvidenceBindsTheCurrentBytesAndMetadataToTheImmutableHumanManifest()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var store = new FileSystemObjectStore(_root.FullName);
        var key = ObjectKey.ForReviewCandidate(data.Data.Generation, PhotoVariant.Card);
        var changed = RandomNumberGenerator.GetBytes(TestImages.WebP(64, 32).Length);
        await store.DeleteAsync(key, Ct);
        using (var content = new MemoryStream(changed))
        {
            await store.PutAsync(key, content, changed.Length, "image/webp", Ct);
        }
        await ExecuteAsync("update media.review_candidate_variants set sha256=$2 where generation=$1 and kind='card'",
            data.Data.Generation, SHA256.HashData(changed));
        var before = await AppealReadProjectionAsync();
        var traced = new AppealReadStore(store);

        Assert.IsType<PhotoReviewEvidenceResult.Unavailable>(await AppealReader(traced).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Card, Ct));

        Assert.Empty(traced.Opened);
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealEvidenceLeaseIsRevokedByReassignmentEvenToTheSameReviewer(bool sameReviewer)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await AppealReader().OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        var reviewer = sameReviewer ? data.Reviewer : await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Appeal);
        Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data with { Reviewer = reviewer }, Key(), 2));
        var before = await AppealReadProjectionAsync();
        var destination = Enumerable.Repeat((byte)0xa5, 8).ToArray();

        await Assert.ThrowsAsync<PhotoReviewEvidenceRevokedException>(() => lease.ReadAsync(destination, Ct));

        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("appeal")]
    [InlineData("session")]
    [InlineData("stamp")]
    [InlineData("deleted")]
    public async Task AppealEvidenceRevalidatesAuthorityAndStateBeforeEveryDeliveredChunk(string change)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await AppealReader().OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        if (change is "evidence" or "appeal")
        {
            await GrantAsync(data.Reviewer.UserId, change == "evidence" ? PhotoReviewCapability.Evidence : PhotoReviewCapability.Appeal, false);
        }
        else if (change == "session")
        {
            await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", data.Reviewer.SessionId, _time.GetUtcNow());
        }
        else
        {
            await ExecuteAsync(change == "stamp" ? "update identity.users set security_stamp='changed' where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1",
                change == "stamp" ? data.Reviewer.UserId : data.Data.Item.UploadId);
        }
        var before = await AppealReadProjectionAsync();
        var destination = Enumerable.Repeat((byte)0xa5, 8).ToArray();

        await Assert.ThrowsAsync<PhotoReviewEvidenceRevokedException>(() => lease.ReadAsync(destination, Ct));

        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Fact]
    public async Task AppealEvidenceReleasesSqlLocksDuringStorageAndRejectsAConcurrentRevocationBeforeDelivery()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var entered = false;
        string? winner = null;
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName), async (_, cancellationToken) =>
        {
            entered = true;
            await using (var db = Context())
            {
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await db.Database.ExecuteSqlAsync($"set local lock_timeout='100ms'", cancellationToken);
                await db.Database.ExecuteSqlAsync($"""
                    select 1 from safety.user_guards where user_id in ({data.Reviewer.UserId},{data.Data.Owner.UserId})
                    order by user_id for update nowait
                    """, cancellationToken);
                await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={data.Data.Item.UploadId} for update nowait", cancellationToken);
                await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={data.Data.Item.CaseId} for update nowait", cancellationToken);
                await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={data.Data.Generation} for update nowait", cancellationToken);
                await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={data.Appeal.AppealId} for update nowait", cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
            }
            await GrantAsync(data.Reviewer.UserId, PhotoReviewCapability.Appeal, false);
            winner = await AppealReadProjectionAsync();
        });

        Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(await AppealReader(store).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));

        Assert.True(entered);
        Assert.NotNull(winner);
        Assert.Equal(winner, await AppealReadProjectionAsync());
    }

    [Fact]
    public async Task AppealEvidenceAuditFailureNeverReturnsALeaseOrChangesReviewState()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var before = await AppealReadProjectionAsync();
        var failure = new RefuseAppealReadAudit();

        await Assert.ThrowsAsync<IOException>(() => AppealReader(interceptors: [failure]).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));

        Assert.True(failure.Fired);
        Assert.Equal(before, await AppealReadProjectionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealEvidenceCommitFailureOrLostResponseNeverReturnsALease(bool afterCommit)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var before = await AppealReadProjectionAsync();
        var history = await AppealDecisionSnapshotAsync();
        var failure = new AppealReadCommitFault(_connectionString, data.Appeal.AppealId, afterCommit);

        await Assert.ThrowsAsync<IOException>(() => AppealReader(interceptors: [failure]).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));

        Assert.True(failure.Fired);
        Assert.Equal(afterCommit ? 1L : 0L, await ScalarAsync(
            "select count(*) from safety.photo_review_appeal_evidence_accesses where appeal_id=$1", data.Appeal.AppealId));
        if (!afterCommit)
        {
            Assert.Equal(before, await AppealReadProjectionAsync());
        }
        Assert.Equal(history, await AppealDecisionSnapshotAsync());
        await AssertAppealAssignmentAsync(data, 2, data.Reviewer.UserId);
    }

    [Fact]
    public async Task AppealEvidenceCannotJoinAnAmbientTransaction()
    {
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppealReader().OpenAsync(default, Guid.NewGuid(), PhotoVariant.Full, Ct));
    }

    [Fact]
    public async Task AppealEvidenceRejectsUnknownVariantsBeforeStorage()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AppealReader(store).OpenAsync(
            data.Reviewer, data.Appeal.AppealId, (PhotoVariant)int.MaxValue, Ct));
        Assert.Empty(store.Opened);
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task AppealEvidencePreservesTheRestrictedOwnersChannelWithoutRequiringAnOwnerSession(string status)
    {
        var data = await AssignedAppealForEvidenceAsync();
        await ExecuteAsync("update identity.users set status=$2 where id=$1", data.Data.Owner.UserId, status);
        await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", data.Data.Owner.SessionId, _time.GetUtcNow());

        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await AppealReader().OpenAsync(
            data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from identity.users where id=$1 and status=$2", data.Data.Owner.UserId, status));
        await AssertAppealAssignmentAsync(data, 2, data.Reviewer.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealEvidenceUsesTheImmutableThirtyDayMaximumNotTheInitialReviewDeadline(bool atMaximum)
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.Advance(TimeSpan.FromDays(30).Subtract(atMaximum ? TimeSpan.Zero : TimeSpan.FromTicks(10)));
        var reviewer = await FreshAppealAssignerSessionAsync(data.Reviewer.UserId);
        var result = await AppealReader().OpenAsync(reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct);
        if (atMaximum)
        {
            Assert.IsType<PhotoReviewEvidenceResult.NotFound>(result);
        }
        else
        {
            var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(result);
            await using var lease = opened.Lease;
            Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        }
    }

    [Fact]
    public async Task AppealEvidenceRejectsAConsistentMutableDeadlineExtensionBeyondTheImmutableHumanMaximum()
    {
        var data = await AssignedAppealForEvidenceAsync();
        await ExecuteAsync("""
            update safety.photo_review_appeals set original_rejected_at=original_rejected_at+interval '1 hour',
              requested_at=requested_at+interval '1 hour',response_target_at=response_target_at+interval '1 hour',
              maximum_retention_until=maximum_retention_until+interval '1 hour' where id=$1
            """, data.Appeal.AppealId);
        await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 hour' where generation=$1", data.Data.Generation);
        _time.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromMinutes(30)));
        var reviewer = await FreshAppealAssignerSessionAsync(data.Reviewer.UserId);
        var before = await AppealReadProjectionAsync();
        var files = Files();
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName));

        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await AppealReader(store).OpenAsync(
            reviewer, data.Appeal.AppealId, PhotoVariant.Full, Ct));

        Assert.Empty(store.Opened);
        Assert.Equal(before, await AppealReadProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task AppealEvidenceCancellationObservesTheActualStorageAttemptWithoutAuditing()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var before = await AppealReadProjectionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var store = new AppealReadStore(new FileSystemObjectStore(_root.FullName), async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                stopped = true;
            }
        });
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = AppealReader(store).OpenAsync(data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, attempt.Token);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, request).WaitAsync(TimeSpan.FromSeconds(5), Ct));
            await attempt.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.True(stopped);
            Assert.Equal(before, await AppealReadProjectionAsync());
        }
        finally
        {
            await attempt.CancelAsync();
            await ObserveAppealReadRequestAsync(request, attempt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealEvidenceRevalidatesARealGuardWaitBeforeReturningAnyBytes(bool revoked)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var guardUser = revoked ? data.Reviewer.UserId : data.Data.Owner.UserId;
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select user_id from safety.user_guards where user_id=$1 for update", locker, held);
        hold.Parameters.AddWithValue(guardUser);
        Assert.Equal(guardUser, await hold.ExecuteScalarAsync(Ct));
        var label = "appeal-evidence-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label }.ConnectionString);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new PhotoReviewAppealEvidence(new ContextFactory(options.Options),
            new FileSystemObjectStore(_root.FullName), _time).OpenAsync(data.Reviewer, data.Appeal.AppealId, PhotoVariant.Full, attempt.Token);
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
            Assert.True(waiting, "Appeal evidence must wait on the reviewer or owner's actual Safety guard.");
            await using var change = new NpgsqlCommand(revoked
                ? "update identity.login_sessions set revoked_at=$2 where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1", locker, held);
            change.Parameters.AddWithValue(revoked ? data.Reviewer.SessionId : data.Data.Item.UploadId);
            if (revoked)
            {
                change.Parameters.AddWithValue(_time.GetUtcNow());
            }
            Assert.Equal(1, await change.ExecuteNonQueryAsync(Ct));
            var winner = await ReadAppealReadProjectionAsync(locker, held, Ct);
            await held.CommitAsync(Ct);
            released = true;
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            if (revoked)
            {
                Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(result);
            }
            else
            {
                Assert.IsType<PhotoReviewEvidenceResult.NotFound>(result);
            }
            Assert.Equal(winner, await AppealReadProjectionAsync());
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
                await ObserveAppealReadRequestAsync(request, attempt);
            }
        }
    }

    private static async Task ObserveAppealReadRequestAsync(Task<PhotoReviewEvidenceResult> request, CancellationTokenSource attempt)
    {
        try
        {
            if (await request is PhotoReviewEvidenceResult.Opened opened)
            {
                await opened.Lease.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (request.IsCanceled && attempt.IsCancellationRequested)
        {
        }
    }

    private async Task<AssignableAppealData> AssignedAppealForEvidenceAsync()
    {
        var data = await AssignableAppealAsync();
        var assigned = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, Key()));
        Assert.Equal(2, assigned.Version);
        return data;
    }

    private PhotoReviewAppealEvidence AppealReader(IObjectStore? store = null, params IInterceptor[] interceptors) =>
        new(Factory(interceptors), store ?? new FileSystemObjectStore(_root.FullName), _time);

    private async Task<string> AppealReadProjectionAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        return await ReadAppealReadProjectionAsync(connection, null, Ct);
    }

    private static async Task<string> ReadAppealReadProjectionAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var existing = await ReadAppealAssignmentProjectionAsync(connection, transaction, cancellationToken);
        await using var catalog = new NpgsqlCommand("select to_regclass('safety.photo_review_appeal_evidence_accesses') is not null", connection, transaction);
        if (await catalog.ExecuteScalarAsync(cancellationToken) is not true)
        {
            return existing + "\n[]";
        }
        await using var records = new NpgsqlCommand("""
            select coalesce(jsonb_agg(to_jsonb(a) order by a.id),'[]'::jsonb)::text
            from safety.photo_review_appeal_evidence_accesses a
            """, connection, transaction);
        return existing + "\n" + Assert.IsType<string>(await records.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class AppealReadStore(IObjectStore inner,
        Func<ObjectKey, CancellationToken, Task>? beforeOpen = null) : IObjectStore
    {
        internal List<ObjectKey> Opened { get; } = [];

        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Opened.Add(key);
            if (beforeOpen is not null)
            {
                await beforeOpen(key, cancellationToken);
            }
            return await inner.OpenReadAsync(key, cancellationToken);
        }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Appeal evidence never writes storage.");

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Appeal evidence never deletes storage.");
    }

    private sealed class RefuseAppealReadAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_appeal_evidence_viewed"))
            {
                Fired = true;
                throw new IOException("Synthetic appeal evidence audit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AppealReadCommitFault(string connectionString, Guid appealId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasAccessAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before appeal evidence commit.");
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
            if (await HasAccessAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost appeal evidence commit response.");
            }
        }

        private async Task<bool> HasAccessAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from safety.photo_review_appeal_evidence_accesses where appeal_id=$1)", connection, transaction);
            command.Parameters.AddWithValue(appealId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }
}
