using System.Data.Common;
using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed class PhotoReviewRejectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-review-rejection-");
    private string _connectionString = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);

    public ValueTask DisposeAsync()
    {
        _root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RejectionCommitsIndependentVersionsHistoryReceiptAndAuditWithoutChangingAnyFiles()
    {
        var (reviewer, item) = await PreparedAsync();
        var before = Files();
        var store = new ReadOnlyStore(new FileSystemObjectStore(_root.FullName));
        var result = Assert.IsType<PhotoReviewRejectionResult.Rejected>(await RejectAsync(reviewer, item.CaseId, 2, Key(), store));

        Assert.Equal(item.CaseId, result.CaseId);
        Assert.Equal(3, result.Version);
        Assert.Equal(item.UploadVersion + 1, result.PhotoVersion);
        await using var db = Context();
        var proof = await db.Database.SqlQuery<DecisionProof>($"""
            select d.id as "Id",d.actor_id as "ActorId",d.case_version as "CaseVersion",d.decision as "Decision",
              d.reason_code as "ReasonCode",d.policy_version as "PolicyVersion",d.candidate_generation as "Generation",
              r.candidate_generation as "CurrentGeneration",d.decided_at as "DecidedAt",
              d.candidate_retention_until_snapshot as "CandidateRetention",d.history_retain_until as "HistoryRetention",
              c.retention_until as "LiveCandidateRetention",u.state as "UploadState",u.rejection_code as "RejectionCode",
              e.target_version as "AuditVersion",e.actor_id as "AuditActorId"
            from safety.photo_review_decisions d join safety.photo_review_cases r on r.id=d.case_id
            join media.uploads u on u.id=r.upload_id join media.review_candidates c on c.generation=d.candidate_generation
            join ops.audit_events e on e.target_id=d.id and e.target_type='photo_review_decision'
            where r.id={item.CaseId} and e.action='safety.photo_review_rejected'
            """).SingleAsync(Ct);
        Assert.NotEqual(Guid.Empty, proof.Id);
        Assert.Equal(reviewer.UserId, proof.ActorId);
        Assert.Equal(reviewer.UserId, proof.AuditActorId);
        Assert.Equal(result.Version, proof.CaseVersion);
        Assert.Equal(proof.CaseVersion, proof.AuditVersion);
        Assert.Equal("reject", proof.Decision);
        Assert.Equal("does_not_meet_policy", proof.ReasonCode);
        Assert.Equal("human-review-v1", proof.PolicyVersion);
        Assert.Equal(proof.CurrentGeneration, proof.Generation);
        Assert.Equal(_time.GetUtcNow(), proof.DecidedAt);
        Assert.Equal(proof.DecidedAt.AddDays(7), proof.CandidateRetention);
        Assert.Equal(proof.CandidateRetention, proof.LiveCandidateRetention);
        Assert.True(proof.HistoryRetention >= proof.DecidedAt.AddYears(1));
        Assert.Equal("Rejected", proof.UploadState);
        Assert.Equal("PHOTO_REVIEW_REJECTED", proof.RejectionCode);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        var manifests = await db.Database.SqlQuery<ManifestProof>($"""
            select variant as "Variant",size_bytes as "SizeBytes",sha256 as "Sha256"
            from safety.photo_review_decision_variants where decision_id={proof.Id} order by variant
            """).ToListAsync(Ct);
        Assert.Equal(3, manifests.Count);
        Assert.Equal(PhotoVariants.All.Select(PhotoVariants.Name).Order(), manifests.Select(m => m.Variant));
        Assert.All(manifests, m =>
        {
            Assert.Equal(TestImages.WebP(64, 32).LongLength, m.SizeBytes);
            Assert.Equal(SHA256.HashData(TestImages.WebP(64, 32)), m.Sha256);
        });
        Assert.Equal(3, store.Opened.Count);
        Assert.All(store.Opened, key => Assert.Equal(StorageArea.ReviewCandidates, key.Area));
        Assert.Equal(PhotoVariants.All.Select(kind => ObjectKey.ForReviewCandidate(proof.Generation, kind)).OrderBy(key => key.Value),
            store.Opened.OrderBy(key => key.Value));
        Assert.Equal(before, Files());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where case_id=$1 and cleanup_requested_at is not null", item.CaseId));
    }

    [Fact]
    public async Task ConfirmedReplayPrecedesMutableVersionAndStorageButNeverBypassesLiveAuthorization()
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var first = Assert.IsType<PhotoReviewRejectionResult.Rejected>(await RejectAsync(reviewer, item.CaseId, 2, key));
        var noStorage = new SubstituteStore(null);
        var replay = Assert.IsType<PhotoReviewRejectionResult.Rejected>(await RejectAsync(reviewer, item.CaseId, 1, key, noStorage));
        Assert.Equal(first, replay);
        Assert.Equal(0, noStorage.OpenCount);
        Assert.IsType<PhotoReviewRejectionResult.KeyReused>(await RejectAsync(reviewer, Guid.NewGuid(), 2, key, noStorage));
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
        Assert.IsType<PhotoReviewRejectionResult.Forbidden>(await RejectAsync(reviewer, item.CaseId, first.Version, key, noStorage));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCommandsHaveOneEffectAndConfirmedSameKeyReplays(bool sameKey)
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var tasks = Enumerable.Range(0, 3).Select(_ => RejectAsync(reviewer, item.CaseId, 2, sameKey ? key : Key())).ToArray();
        var results = await Task.WhenAll(tasks);
        if (sameKey)
        {
            Assert.All(results, r => Assert.IsType<PhotoReviewRejectionResult.Rejected>(r));
            Assert.Single(results.Distinct());
        }
        else
        {
            Assert.Single(results.OfType<PhotoReviewRejectionResult.Rejected>());
            Assert.All(results.Where(r => r is not PhotoReviewRejectionResult.Rejected), r =>
                Assert.True(r is PhotoReviewRejectionResult.StateConflict or PhotoReviewRejectionResult.PreconditionFailed));
        }
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("session")]
    [InlineData("totp")]
    [InlineData("role")]
    public async Task MissingLiveDecisionAuthorityIsRejectedBeforeCaseLookup(string change)
    {
        var (reviewer, item) = await PreparedAsync();
        if (change == "permission")
        {
            await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
        }
        else if (change == "session")
        {
            await ExecuteAsync("update identity.login_sessions set revoked_at=now() where id=$1", reviewer.SessionId);
        }
        else if (change == "totp")
        {
            await ExecuteAsync("update identity.login_sessions set totp_verified_at=null where id=$1", reviewer.SessionId);
        }
        else
        {
            await ExecuteAsync("delete from identity.user_roles where user_id=$1", reviewer.UserId);
        }
        var service = new PhotoReviewRejections(Factory(new RefuseCaseLookup()), new SubstituteStore(null), _time);
        Assert.IsType<PhotoReviewRejectionResult.Forbidden>(await service.RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
    }

    [Fact]
    public async Task EvidenceRevocationDoesNotImplicitlyRevokeTheIndependentDecisionCapability()
    {
        var (reviewer, item) = await PreparedAsync();
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: false);
        await using (var db = Context())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
            var access = new PhotoReviewAccess(db, _time);
            Assert.False(await access.AuthorizeAsync(reviewer, PhotoReviewCapability.Evidence, Ct));
            Assert.True(await access.AuthorizeAsync(reviewer, PhotoReviewCapability.Decide, Ct));
        }
        Assert.IsType<PhotoReviewRejectionResult.Rejected>(await RejectAsync(reviewer, item.CaseId, 2, Key()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("hash")]
    public async Task MissingOrCorruptEvidenceNeverCreatesAHumanRejection(string failure)
    {
        var (reviewer, item) = await PreparedAsync();
        var bytes = TestImages.WebP(64, 32);
        byte[]? replacement = failure switch
        {
            "missing" => null,
            "short" => bytes[..^1],
            "long" => [.. bytes, (byte)1],
            _ => [.. bytes],
        };
        if (failure == "hash")
        {
            replacement![^1] ^= 1;
        }
        Assert.IsType<PhotoReviewRejectionResult.Unavailable>(await RejectAsync(reviewer, item.CaseId, 2, Key(), new SubstituteStore(replacement)));
        await AssertUnchangedAsync(item);
    }

    [Theory]
    [InlineData(PhotoVariant.Thumb)]
    [InlineData(PhotoVariant.Card)]
    [InlineData(PhotoVariant.Full)]
    public async Task EveryDistinctVariantMustBeVerifiedBeforeRecordingAHumanDecision(PhotoVariant corrupted)
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new CorruptVariantStore(new FileSystemObjectStore(_root.FullName), corrupted);
        Assert.IsType<PhotoReviewRejectionResult.Unavailable>(await RejectAsync(reviewer, item.CaseId, 2, Key(), store));
        await AssertUnchangedAsync(item);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("decision")]
    [InlineData("manifest")]
    [InlineData("receipt")]
    public async Task AFailureInAnyPartOfTheCommitRollsBackTheWholeHumanDecision(string failure)
    {
        var (reviewer, item) = await PreparedAsync();
        var before = Files();
        var service = new PhotoReviewRejections(Factory(new RefuseDecisionWrite(failure)), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        await AssertUnchangedAsync(item);
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task StaleVersionsAndAnotherReviewersCaseNeverCreateADecision()
    {
        var (reviewer, item) = await PreparedAsync();
        var foreign = await ModeratorAsync(PhotoReviewCapability.Decide);
        Assert.IsType<PhotoReviewRejectionResult.NotFound>(await RejectAsync(foreign, item.CaseId, 2, Key()));
        Assert.IsType<PhotoReviewRejectionResult.NotFound>(await RejectAsync(reviewer, Guid.NewGuid(), 2, Key()));
        Assert.IsType<PhotoReviewRejectionResult.PreconditionFailed>(await RejectAsync(reviewer, item.CaseId, 1, Key()));
        await AssertUnchangedAsync(item);
    }

    [Fact]
    public async Task UnknownReasonsNeverOpenStorageOrCreateAnEditorialDecision()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new SubstituteStore(null);
        var service = new PhotoReviewRejections(Factory(), store, _time);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RejectAsync(reviewer, item.CaseId, 2,
            (PhotoReviewRejectionReason)int.MaxValue, Key(), Ct));
        Assert.Equal(0, store.OpenCount);
        await AssertUnchangedAsync(item);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("totp")]
    [InlineData("owner")]
    [InlineData("deleted")]
    [InlineData("reassigned")]
    public async Task ChangesDuringStorageAreNotBlockedBySqlAndPreventTheCommit(string change)
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new BlockingStore(new FileSystemObjectStore(_root.FullName));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var opening = new PhotoReviewRejections(Factory(), store, _time).RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), budget.Token);
        try
        {
            Assert.Same(store.Entered.Task, await Task.WhenAny(store.Entered.Task, opening).WaitAsync(budget.Token));
            if (change == "grant")
            {
                await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
            }
            else if (change == "totp")
            {
                _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(10));
            }
            else if (change == "owner")
            {
                await ExecuteAsync("update identity.users set status='Restricted' where id=$1", item.OwnerId);
            }
            else if (change == "deleted")
            {
                await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);
            }
            else
            {
                var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
                var replacement = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
                await using var db = Context();
                Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await new PhotoReviewAssignments(db, _time)
                    .AssignAsync(assigner, item.CaseId, replacement.UserId, 2, Key(), Ct));
            }
            store.Release();
            var result = await opening.WaitAsync(budget.Token);
            if (change is "owner" or "deleted" or "reassigned")
            {
                Assert.IsType<PhotoReviewRejectionResult.NotFound>(result);
            }
            else
            {
                Assert.IsType<PhotoReviewRejectionResult.Forbidden>(result);
            }
            if (change == "deleted")
            {
                await AssertNoDecisionAsync(item);
                Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Deleted' and version=4", item.UploadId));
                Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1 and version=2", item.CaseId));
            }
            else if (change == "reassigned")
            {
                await AssertNoDecisionAsync(item);
                Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview' and version=3", item.UploadId));
                Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1 and version=3", item.CaseId));
            }
            else
            {
                await AssertUnchangedAsync(item);
            }
        }
        finally
        {
            store.Release();
            await budget.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await opening.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (opening.IsCompleted)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrAmbiguousCommitNeverReportsAConfirmedDecision(bool afterCommit)
    {
        var (reviewer, item) = await PreparedAsync();
        var failure = new RefuseDecisionCommit(_connectionString, item.CaseId, afterCommit);
        var service = new PhotoReviewRejections(Factory(failure), new FileSystemObjectStore(_root.FullName), _time);

        await Assert.ThrowsAsync<IOException>(() => service.RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));

        Assert.True(failure.Fired);
        var count = afterCommit ? 1L : 0L;
        Assert.Equal(count, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(count, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        Assert.Equal(count, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
        Assert.Equal(count, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Rejected'", item.UploadId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TotpThatExpiresDuringARealGuardOrCaseLockWaitCannotAuthorizeARejection(bool ownerGuard)
    {
        var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ActorAsync(AccountRoles.Moderator, Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"));
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: true);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: true);
        var item = await CaseAsync(Guid.Parse("00000000-0000-0000-0000-00000000000a"));
        await using (var db = Context())
        {
            Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await new PhotoReviewAssignments(db, _time)
                .AssignAsync(assigner, item.CaseId, reviewer.UserId, 1, Key(), Ct));
        }
        var application = "rejection-lock-" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = application }.ConnectionString;
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(connectionString);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(budget.Token);
        await using var held = await connection.BeginTransactionAsync(budget.Token);
        var sql = ownerGuard ? "select 1 from safety.user_guards where user_id=$1 for update"
            : "select 1 from safety.photo_review_cases where id=$1 for update";
        await using (var command = new NpgsqlCommand(sql, connection, held))
        {
            command.Parameters.AddWithValue(ownerGuard ? item.OwnerId : item.CaseId);
            await command.ExecuteNonQueryAsync(budget.Token);
        }
        var released = false;
        var rejecting = new PhotoReviewRejections(new ContextFactory(options.Options), new FileSystemObjectStore(_root.FullName), _time)
            .RejectAsync(reviewer, item.CaseId, 2, PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), budget.Token);
        try
        {
            await WaitForApplicationLockAsync(application, rejecting, budget.Token);
            _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(10));
            await held.CommitAsync(budget.Token);
            released = true;
            Assert.IsType<PhotoReviewRejectionResult.Forbidden>(await rejecting.WaitAsync(budget.Token));
            await AssertUnchangedAsync(item);
            await using var proof = Context();
            await using var transaction = await proof.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
            Assert.True(await new PhotoReviewAccess(proof, _time).AuthorizeAsync(reviewer, PhotoReviewCapability.Evidence, Ct));
            Assert.False(await new PhotoReviewAccess(proof, _time).AuthorizeAsync(reviewer, PhotoReviewCapability.Decide, Ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!released)
            {
                await held.RollbackAsync(cleanup.Token);
            }
            await budget.CancelAsync();
            try
            {
                await rejecting.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (rejecting.IsCompleted)
            {
            }
        }
    }

    private async Task WaitForApplicationLockAsync(string application, Task operation, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where application_name=$1 and wait_event_type='Lock')", connection);
            command.Parameters.AddWithValue(application);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }
            Assert.False(operation.IsCompleted, "The decision must actually wait for the held guard or case row.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        Assert.Fail("The decision did not enter a real PostgreSQL lock wait.");
    }

    private Task<PhotoReviewRejectionResult> RejectAsync(PhotoReviewActor actor, Guid caseId, long version,
        IdempotencyKey key, IObjectStore? store = null) => new PhotoReviewRejections(Factory(), store ?? new FileSystemObjectStore(_root.FullName), _time)
        .RejectAsync(actor, caseId, version, PhotoReviewRejectionReason.DoesNotMeetPolicy, key, Ct);

    private async Task AssertUnchangedAsync(CaseData item)
    {
        await AssertNoDecisionAsync(item);
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview' and version=3", item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1 and version=2", item.CaseId));
    }

    private async Task AssertNoDecisionAsync(CaseData item)
    {
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
    }

    private string[] Files() => [.. Directory.GetFiles(_root.FullName, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(_root.FullName, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))];

    private async Task<(PhotoReviewActor Reviewer, CaseData Item)> PreparedAsync()
    {
        var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        await using var db = Context();
        Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await new PhotoReviewAssignments(db, _time)
            .AssignAsync(assigner, item.CaseId, reviewer.UserId, 1, Key(), Ct));
        return (reviewer, item);
    }

    private ContextFactory Factory(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(_connectionString).AddInterceptors(interceptors);
        return new ContextFactory(options.Options);
    }

    private DuoraDbContext Context(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(_connectionString).AddInterceptors(interceptors);
        return new DuoraDbContext(options.Options);
    }

    private async Task<PhotoReviewActor> ModeratorAsync(params PhotoReviewCapability[] capabilities)
    {
        var actor = await ActorAsync(AccountRoles.Moderator);
        foreach (var capability in capabilities)
        {
            await GrantAsync(actor.UserId, capability, enabled: true);
        }
        return actor;
    }

    private async Task<PhotoReviewActor> ActorAsync(string role, Guid? userId = null)
    {
        var user = userId ?? Guid.NewGuid();
        var session = Guid.NewGuid();
        var now = _time.GetUtcNow();
        var hash = SHA256.HashData(session.ToByteArray());
        var ticket = new byte[] { 1 };
        var policy = role == AccountRoles.Participant ? "Normal" : "Administrative";
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.users(id,status,created_at,email_confirmed,phone_number_confirmed,
              two_factor_enabled,lockout_enabled,access_failed_count,security_stamp)
            values({user},'Active',{now},true,false,true,true,0,'synthetic-assignment-stamp')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.user_roles(user_id,role_id) select {user},id from identity.roles where name={role}
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.login_sessions(id,user_id,key_hash,ticket,security_stamp,description,policy,
              created_at,last_seen_at,idle_expires_at,absolute_expires_at,reauthenticated_at,mfa_verified_at,totp_verified_at)
            values({session},{user},{hash},{ticket},'synthetic-assignment-stamp','synthetic',{policy},
              {now.AddMinutes(-1)},{now},{now.AddMinutes(10)},{now.AddHours(1)},{now},{now},{now})
            """, Ct);
        await transaction.CommitAsync(Ct);
        return new PhotoReviewActor(user, session);
    }

    private async Task GrantAsync(Guid userId, PhotoReviewCapability capability, bool enabled)
    {
        await using var db = Context();
        Assert.True(OperatorName.TryParse("assignment-test", out var operatorName));
        Assert.True(await new PhotoReviewAccess(db, _time).SetCapabilityAsync(userId, capability, enabled, operatorName, Ct));
    }

    private async Task<CaseData> CaseAsync(Guid? ownerId = null)
    {
        var owner = await ActorAsync(AccountRoles.Participant, ownerId);
        var original = TestImages.Jpeg(64, 32);
        var store = new FileSystemObjectStore(_root.FullName);
        var upload = Upload.Reserve(owner.UserId, Key(), PhotoFormat.Jpeg, original.Length, _time.GetUtcNow());
        await using (var db = Context())
        {
            db.Add(upload);
            await db.SaveChangesAsync(Ct);
            var hash = SHA256.HashData(original);
            await db.Database.ExecuteSqlAsync($"""
                update media.uploads set state='Processing',version=version+1,content_sha256={hash} where id={upload.Id}
                """, Ct);
        }

        using (var bytes = new MemoryStream(original))
        {
            await store.PutAsync(ObjectKey.Parse(upload.QuarantineKey), bytes, original.Length, "image/jpeg", Ct);
        }
        await using (var db = Context())
        {
            var stage = new PhotoReviewCandidates(db, store, _time, new MediaProcessingOptions());
            Assert.IsType<ReviewCandidateStageResult.Staged>(await stage.StageAsync(upload.Id, 2,
                _time.GetUtcNow().AddMinutes(1), new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 64, 32,
                    [.. PhotoVariants.All.Select(kind => new SanitizedVariant(kind, TestImages.WebP(64, 32), 64, 32))]), Ct));
        }

        await using var read = Context();
        return await read.Database.SqlQuery<CaseData>($"""
            select r.id as "CaseId",u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",
              r.awaiting_review_at as "AwaitingReviewAt",r.review_deadline as "ReviewDeadline"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where u.id={upload.Id}
            """).SingleAsync(Ct);
    }

    private static IdempotencyKey Key()
    {
        Assert.True(IdempotencyKey.TryParse(Guid.NewGuid().ToString("N"), out var key));
        return key;
    }

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 2 };
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 2 };
        if (id.HasValue)
        {
            command.Parameters.AddWithValue(id.Value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CaseData(Guid CaseId, Guid OwnerId, Guid UploadId, long UploadVersion,
        DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline);

    private sealed record DecisionProof(Guid Id, Guid ActorId, long CaseVersion, string Decision, string ReasonCode,
        string PolicyVersion, Guid Generation, Guid CurrentGeneration, DateTimeOffset DecidedAt,
        DateTimeOffset CandidateRetention, DateTimeOffset HistoryRetention, DateTimeOffset LiveCandidateRetention,
        string UploadState, string RejectionCode, long AuditVersion, Guid AuditActorId);

    private sealed record ManifestProof(string Variant, long SizeBytes, byte[] Sha256);

    private sealed class ContextFactory(DbContextOptions<DuoraDbContext> options) : IDbContextFactory<DuoraDbContext>
    {
        public DuoraDbContext CreateDbContext() => new(options);
    }

    private sealed class ReadOnlyStore(IObjectStore inner) : IObjectStore
    {
        internal List<ObjectKey> Opened { get; } = [];
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejection may not write media.");
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejection retains its candidate for an appeal.");
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Opened.Add(key);
            return inner.OpenReadAsync(key, cancellationToken);
        }
    }

    private sealed class SubstituteStore(byte[]? bytes) : IObjectStore
    {
        internal int OpenCount { get; private set; }
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<Stream?>(bytes is null ? null : new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class BlockingStore(IObjectStore inner) : IObjectStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _released.TrySetResult();
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return await inner.OpenReadAsync(key, cancellationToken);
        }
    }

    private sealed class CorruptVariantStore(IObjectStore inner, PhotoVariant corrupted) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            if (!key.Value.EndsWith("-" + PhotoVariants.Name(corrupted), StringComparison.Ordinal))
            {
                return inner.OpenReadAsync(key, cancellationToken);
            }
            var bytes = TestImages.WebP(64, 32);
            bytes[^1] ^= 1;
            return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class RefuseCaseLookup : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("safety.photo_review_cases", command.CommandText, StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RefuseDecisionWrite(string failure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var target = failure switch
            {
                "audit" => "ops.audit_events",
                "decision" => "safety.photo_review_decisions",
                "manifest" => "safety.photo_review_decision_variants",
                _ => "safety.photo_review_decision_commands",
            };
            if (command.CommandText.Contains("insert into " + target, StringComparison.Ordinal))
            {
                throw new IOException("Synthetic decision write refusal");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RefuseDecisionCommit(string connectionString, Guid caseId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasDecisionAuditAsync(
                Assert.IsType<NpgsqlConnection>(transaction.Connection), Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before decision commit");
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
            if (await HasDecisionAuditAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost response after decision commit");
            }
        }

        private async Task<bool> HasDecisionAuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("""
                select exists(select 1 from safety.photo_review_decisions d join ops.audit_events e
                  on e.target_id=d.id and e.target_type='photo_review_decision'
                  where d.case_id=$1 and e.action='safety.photo_review_rejected')
                """, connection, transaction);
            command.Parameters.AddWithValue(caseId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }
}
