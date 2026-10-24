using System.Collections.Concurrent;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-review-approval-");
    private string _connectionString = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);

    public ValueTask DisposeAsync()
    {
        _root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ApprovalCommitsTheAssetVersionsDecisionReceiptAndCandidateCleanupAtomically()
    {
        var (reviewer, item) = await PreparedAsync();
        var files = Files();
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName));
        await using var sourceContext = Context();
        var candidateGeneration = await sourceContext.Database.SqlQuery<Guid>($"""
            select candidate_generation as "Value" from safety.photo_review_cases where id={item.CaseId}
            """).SingleAsync(Ct);

        var result = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key(), store));

        Assert.Equal(item.CaseId, result.CaseId);
        Assert.Equal(3, result.Version);
        Assert.Equal(item.UploadVersion + 1, result.PhotoVersion);
        Assert.NotEqual(Guid.Empty, result.AssetId);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.media_assets a join media.uploads u on u.id=a.upload_id
            join media.review_promotions p on p.asset_id=a.id
            join safety.photo_review_cases r on r.id=p.case_id
            where a.id=$1 and a.state='Approved' and a.original_key is null and a.version=1
              and a.sha256=u.content_sha256 and u.state='Approved' and u.rejection_code is null
              and u.version=4 and r.version=3 and p.case_version=2 and p.upload_version=3
              and p.committed_at is not null and p.writer_settled_at is not null
              and p.cleanup_requested_at is null and p.deleted_at is null and p.cleanup_token is null
            """, result.AssetId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_decisions d
            join safety.photo_review_cases r on r.id=d.case_id
            join media.review_candidates c on c.generation=d.candidate_generation
            join ops.audit_events e on e.target_id=d.id
            where d.case_id=$1 and d.decision='approve' and d.reason_code='meets_policy'
              and d.case_version=3 and d.candidate_generation=r.candidate_generation
              and d.policy_version=r.policy_version and d.actor_id=$2
              and d.history_retain_until >= d.decided_at+interval '1 year'
              and d.candidate_retention_until_snapshot=d.decided_at+interval '24 hours'
              and c.retention_until=d.candidate_retention_until_snapshot and c.committed_at is not null
              and c.cleanup_requested_at=d.decided_at and c.deleted_at is null
              and e.action='safety.photo_review_approved' and e.target_type='photo_review_decision'
              and e.target_version=d.case_version and e.actor_id=d.actor_id
              and (select count(*) from safety.photo_review_decision_variants v where v.decision_id=d.id)=3
            """, item.CaseId, reviewer.UserId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_decision_commands
            where case_id=$1 and operation='decide' and result_version=3 and result_photo_version=4 and result_asset_id=$2
            """, item.CaseId, result.AssetId));
        Assert.Equal(3, store.PutAttempts);
        Assert.Equal(0, store.DeleteAttempts);
        Assert.Collection(store.Opened.Select(key => key.Value).Order(),
            key => Assert.Equal($"review-candidates/{candidateGeneration:N}-card", key),
            key => Assert.Equal($"review-candidates/{candidateGeneration:N}-full", key),
            key => Assert.Equal($"review-candidates/{candidateGeneration:N}-thumb", key));
        await using var db = Context();
        var variants = await db.Database.SqlQuery<VariantProof>($"""
            select v.kind as "Kind",v.object_key as "ObjectKey",v.size_bytes as "SizeBytes",p.generation as "Generation",
              cv.size_bytes as "CandidateSize",pv.size_bytes as "PromotionSize",dv.size_bytes as "DecisionSize",
              cv.sha256 as "CandidateSha256",pv.sha256 as "PromotionSha256",dv.sha256 as "DecisionSha256"
            from media.media_variants v join media.review_promotions p on p.asset_id=v.media_asset_id
            join media.review_promotion_variants pv on pv.generation=p.generation and pv.kind=v.kind
            join media.review_candidate_variants cv on cv.generation=p.candidate_generation and cv.kind=v.kind
            join safety.photo_review_decisions d on d.case_id=p.case_id and d.case_version={result.Version}
            join safety.photo_review_decision_variants dv on dv.decision_id=d.id and dv.variant=v.kind
            where v.media_asset_id={result.AssetId} and pv.object_key=v.object_key
              and cv.object_key='review-candidates/' || replace({candidateGeneration}::text,'-','') || '-' || v.kind
              and v.width=pv.width and v.height=pv.height and pv.width=cv.width and pv.height=cv.height
            order by v.kind
            """).ToListAsync(Ct);
        Assert.Equal(3, variants.Count);
        Assert.Collection(variants,
            variant => Assert.Equal("card", variant.Kind),
            variant => Assert.Equal("full", variant.Kind),
            variant => Assert.Equal("thumb", variant.Kind));
        foreach (var variant in variants)
        {
            Assert.Equal(variant.CandidateSize, variant.SizeBytes);
            Assert.Equal(variant.CandidateSize, variant.PromotionSize);
            Assert.Equal(variant.CandidateSize, variant.DecisionSize);
            Assert.Equal(SHA256.HashData(TestImages.WebP(64, 32)), variant.CandidateSha256);
            Assert.Equal(variant.CandidateSha256, variant.PromotionSha256);
            Assert.Equal(variant.CandidateSha256, variant.DecisionSha256);
            Assert.Equal($"derivatives/review-{variant.Generation:N}-{variant.Kind}", variant.ObjectKey);
            await using var bytes = await store.OpenReadAsync(ObjectKey.Parse(variant.ObjectKey), Ct);
            Assert.NotNull(bytes);
            using var content = new MemoryStream();
            await bytes.CopyToAsync(content, Ct);
            Assert.Equal(TestImages.WebP(64, 32), content.ToArray());
            Assert.Equal(content.Length, variant.SizeBytes);
        }

        Assert.All(files, file => Assert.Contains(file, Files()));
        Assert.Equal(files.Length + 3, Files().Length);
    }

    [Fact]
    public async Task ApprovalReplayUsesItsHistoricalReceiptBeforeMutableStateButRequiresCurrentAuthority()
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var first = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key));
        await ExecuteAsync("update media.media_assets set state='Deleted',deleted_at=$2,version=version+1 where id=$1", first.AssetId, _time.GetUtcNow());
        await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var replay = await ApproveAsync(reviewer, item, key, noStorage, version: 1);
        Assert.Equal(first, Assert.IsType<PhotoReviewApprovalResult.Approved>(replay));
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Empty(noStorage.Opened);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(await ApproveAsync(reviewer, item, key, noStorage));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalAndRejectionShareOneCommandNamespaceWithDifferentIntentions(bool approveFirst)
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        if (approveFirst)
        {
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key));
            Assert.IsType<PhotoReviewRejectionResult.KeyReused>(await new PhotoReviewRejections(Factory(), noStorage, _time)
                .RejectAsync(reviewer, item.CaseId, 1, PhotoReviewRejectionReason.DoesNotMeetPolicy, key, Ct));
        }
        else
        {
            Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(), new FileSystemObjectStore(_root.FullName), _time)
                .RejectAsync(reviewer, item.CaseId, 2, PhotoReviewRejectionReason.DoesNotMeetPolicy, key, Ct));
            Assert.IsType<PhotoReviewApprovalResult.KeyReused>(await ApproveAsync(reviewer, item, key, noStorage, version: 1));
        }

        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Empty(noStorage.Opened);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("role")]
    [InlineData("mfa")]
    [InlineData("session")]
    [InlineData("stamp")]
    [InlineData("totp")]
    public async Task ApprovalRechecksAuthorityAfterPrivateCopiesWithoutHoldingSqlLocksDuringStorage(string change)
    {
        var (reviewer, item) = await PreparedAsync();
        var changed = false;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), beforePut: async _ =>
        {
            if (changed)
            {
                return;
            }

            changed = true;
            switch (change)
            {
                case "grant":
                    await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
                    break;
                case "role":
                    await ExecuteAsync("delete from identity.user_roles where user_id=$1", reviewer.UserId);
                    break;
                case "mfa":
                    await ExecuteAsync("update identity.users set two_factor_enabled=false where id=$1", reviewer.UserId);
                    break;
                case "session":
                    await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", reviewer.SessionId, _time.GetUtcNow());
                    break;
                case "stamp":
                    await ExecuteAsync("update identity.users set security_stamp='changed-synthetic-stamp' where id=$1", reviewer.UserId);
                    break;
                default:
                    await ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1", reviewer.SessionId, _time.GetUtcNow().AddMinutes(-5));
                    break;
            }
        });

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(await ApproveAsync(reviewer, item, Key(), store));
        Assert.True(changed);
        Assert.True(store.PutAttempts > 0);
        Assert.Equal(0, store.DeleteAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalRechecksOwnerEligibilityAfterItsPrivateIo()
    {
        var (reviewer, item) = await PreparedAsync();
        var changed = false;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), beforePut: async _ =>
        {
            if (!changed)
            {
                await ExecuteAsync("update identity.users set status='Restricted' where id=$1", item.OwnerId);
                changed = true;
            }
        });
        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(await ApproveAsync(reviewer, item, Key(), store));
        Assert.True(changed);
        Assert.True(store.PutAttempts > 0);
        Assert.Equal(0, store.DeleteAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task AStaleCaseVersionCannotStartApprovalCopies()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        Assert.IsType<PhotoReviewApprovalResult.PreconditionFailed>(await ApproveAsync(reviewer, item, Key(), store, version: 1));
        Assert.Empty(store.Opened);
        Assert.Equal(0, store.PutAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentApprovalCommandsHaveOneAssetAndNeverOverwriteTheWinner(bool sameKey)
    {
        var (reviewer, item) = await PreparedAsync();
        var firstKey = Key();
        var before = Files();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var barrier = new ApprovalCopiesBarrier(attempt.Token);
        var firstStore = new ApprovalStore(new FileSystemObjectStore(_root.FullName), afterPut: barrier.AfterPutAsync);
        var secondStore = new ApprovalStore(new FileSystemObjectStore(_root.FullName), afterPut: barrier.AfterPutAsync);
        Task<PhotoReviewApprovalResult>? first = null;
        Task<PhotoReviewApprovalResult>? second = null;
        PhotoReviewApprovalResult[] results;
        try
        {
            first = new PhotoReviewApprovals(Factory(), firstStore, _time)
                .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, firstKey, attempt.Token);
            second = new PhotoReviewApprovals(Factory(), secondStore, _time)
                .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, sameKey ? firstKey : Key(), attempt.Token);
            await barrier.BothReady.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(2, barrier.Generations.Count);
            Assert.Equal(3, firstStore.PutAttempts);
            Assert.Equal(3, secondStore.PutAttempts);
            Assert.Equal(2, await ScalarAsync("select count(*) from media.review_promotions where case_id=$1 and committed_at is null and writer_settled_at is null", item.CaseId));
            Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
            barrier.Release.TrySetResult();
            results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            barrier.Release.TrySetResult();
            await attempt.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task[] accepted = [.. new Task?[] { first, second }.OfType<Task>()];
            var observed = Task.WhenAll(accepted);
            try
            {
                await observed.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (observed.IsCompleted && observed.IsCanceled && attempt.IsCancellationRequested)
            {
                // Both accepted commands have finished before contexts, buffers and fixture are released.
            }
        }
        if (sameKey)
        {
            Assert.Equal(Assert.IsType<PhotoReviewApprovalResult.Approved>(results[0]),
                Assert.IsType<PhotoReviewApprovalResult.Approved>(results[1]));
        }
        else
        {
            Assert.Single(results, result => result is PhotoReviewApprovalResult.Approved);
            Assert.Single(results, result => result is PhotoReviewApprovalResult.StateConflict);
        }

        Assert.Equal(1, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        Assert.Equal(3, await ScalarAsync("select count(*) from media.media_variants v join media.media_assets a on a.id=v.media_asset_id where a.upload_id=$1", item.UploadId));
        Assert.All(before, file => Assert.Contains(file, Files()));
        await using var db = Context();
        var keys = await db.Database.SqlQuery<string>($"""
            select v.object_key as "Value" from media.media_variants v join media.media_assets a on a.id=v.media_asset_id
            where a.upload_id={item.UploadId}
            """).ToListAsync(Ct);
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var key in keys)
        {
            await using var stream = await store.OpenReadAsync(ObjectKey.Parse(key), Ct);
            Assert.NotNull(stream);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, Ct);
            Assert.Equal(TestImages.WebP(64, 32), bytes.ToArray());
        }
    }

    [Fact]
    public async Task AModeratorWithoutAnExplicitDecideGrantCannotLocateOrCopyTheCase()
    {
        var (_, item) = await PreparedAsync();
        var unauthorized = await ModeratorAsync(PhotoReviewCapability.Evidence);
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var service = new PhotoReviewApprovals(Factory(new RefuseCaseLookup()), noStorage, _time);
        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(await service.ApproveAsync(unauthorized, item.CaseId, 2,
            PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct));
        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(await service.ApproveAsync(unauthorized, Guid.NewGuid(), 2,
            PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct));
        Assert.Empty(noStorage.Opened);
        Assert.Equal(0, noStorage.PutAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task UnknownApprovalReasonsAreRejectedBeforeAnyCaseOrStorageAccess()
    {
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var service = new PhotoReviewApprovals(Factory(), store, _time);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ApproveAsync(default, Guid.NewGuid(), 1,
            (PhotoReviewApprovalReason)999, Key(), Ct));
        Assert.Empty(store.Opened);
        Assert.Equal(0, store.PutAttempts);
        Assert.Equal(0, store.DeleteAttempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFailedFinalCopyCannotCommitAnApprovalAndKeepsItsGenerationOwnedCleanup(int failingPut)
    {
        var (reviewer, item) = await PreparedAsync();
        var before = Files();
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), failingPut: failingPut);
        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(await ApproveAsync(reviewer, item, Key(), store));
        Assert.Equal(failingPut, store.PutAttempts);
        Assert.Equal(0, store.DeleteAttempts);
        Assert.All(before, file => Assert.Contains(file, Files()));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_promotions where case_id=$1 and committed_at is null and cleanup_requested_at is not null and writer_settled_at is null", item.CaseId));
        await AssertNoApprovalAsync(item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUncertainApprovalCommitNeverConfirmsAnUnobservedResultAndReplayProvesTheCommit(bool afterCommit)
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var fault = new ApprovalCommitFault(_connectionString, item.UploadId, afterCommit);
        var service = new PhotoReviewApprovals(Factory(fault), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, key, Ct));
        Assert.True(fault.Fired);
        Assert.Equal(afterCommit ? 1 : 0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
        Assert.Equal(afterCommit ? 1 : 0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        if (afterCommit)
        {
            var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key, noStorage, version: 1));
            Assert.Equal(0, noStorage.PutAttempts);
            Assert.Empty(noStorage.Opened);
        }
        else
        {
            await AssertNoApprovalAsync(item);
        }
    }

    [Theory]
    [InlineData("thumb", false)]
    [InlineData("card", false)]
    [InlineData("full", false)]
    [InlineData("thumb", true)]
    [InlineData("card", true)]
    [InlineData("full", true)]
    public async Task MissingOrCorruptCandidateBytesCannotBecomeAnApprovedAsset(string kind, bool corrupt)
    {
        var (reviewer, item) = await PreparedAsync();
        await using var db = Context();
        var key = ObjectKey.Parse(await db.Database.SqlQuery<string>($"""
            select v.object_key as "Value" from media.review_candidate_variants v
            join safety.photo_review_cases r on r.candidate_generation=v.generation
            where r.id={item.CaseId} and v.kind={kind}
            """).SingleAsync(Ct));
        var inner = new FileSystemObjectStore(_root.FullName);
        if (corrupt)
        {
            var bytes = TestImages.WebP(64, 32);
            bytes[^1] ^= 1;
            using var replacement = new MemoryStream(bytes);
            await inner.PutAsync(key, replacement, bytes.Length, "image/webp", Ct);
        }
        else
        {
            await inner.DeleteAsync(key, Ct);
        }

        var before = Files();
        var store = new ApprovalStore(inner);
        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(await ApproveAsync(reviewer, item, Key(), store));
        Assert.Equal(0, store.DeleteAttempts);
        Assert.All(before, file => Assert.Contains(file, Files()));
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task AFailedApprovalAuditRollsBackEveryApprovalProjection()
    {
        var (reviewer, item) = await PreparedAsync();
        var candidateBefore = await CandidateMetadataAsync(item);
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName));
        var fault = new RefuseApprovalAudit();
        var service = new PhotoReviewApprovals(Factory(fault), store, _time);
        await Assert.ThrowsAsync<IOException>(() => service.ApproveAsync(reviewer, item.CaseId, 2,
            PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct));
        Assert.True(fault.Fired);
        Assert.Equal(3, store.PutAttempts);
        Assert.Equal(0, store.DeleteAttempts);
        await AssertNoApprovalAsync(item);
        Assert.Equal(candidateBefore, await CandidateMetadataAsync(item));
    }

    private async Task<string> CandidateMetadataAsync(CaseData item)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select to_jsonb(c)::text as "Value" from media.review_candidates c
            join safety.photo_review_cases r on r.candidate_generation=c.generation where r.id={item.CaseId}
            """).SingleAsync(Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAndApprovalCannotCommitOwnershipOfTheSamePromotion(bool cleanupFirst)
    {
        var (reviewer, item) = await PreparedAsync();
        var inner = new FileSystemObjectStore(_root.FullName);
        var before = Files();
        var store = new ApprovalStore(inner);
        var collector = new CollectConfirmedPreparation(this, store, inner, cleanupFirst);
        var service = new PhotoReviewApprovals(Factory(collector), store, _time);
        var result = await service.ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct);
        if (cleanupFirst)
        {
            Assert.True(collector.Fired);
            Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
            await AssertNoApprovalAsync(item);
            Assert.Equal(1, await ScalarAsync("select count(*) from media.review_promotions where case_id=$1 and deleted_at is not null", item.CaseId));
        }
        else
        {
            var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(result);
            _time.Advance(TimeSpan.FromSeconds(16));
            await using var source = NpgsqlDataSource.Create(_connectionString);
            var gc = new ReviewPromotionCleanupDispatcher(source, inner, _time, enabled: true);
            Assert.Null(await gc.ClaimAsync(Ct));
            Assert.Equal(0, await gc.DispatchOnceAsync(Ct));
            Assert.Equal(3, await ScalarAsync("select count(*) from media.media_variants where media_asset_id=$1", approved.AssetId));
            await using var db = Context();
            var keys = await db.Database.SqlQuery<string>($"""
                select object_key as "Value" from media.media_variants where media_asset_id={approved.AssetId}
                """).ToListAsync(Ct);
            foreach (var key in keys)
            {
                await using var stream = await inner.OpenReadAsync(ObjectKey.Parse(key), Ct);
                Assert.NotNull(stream);
            }
        }

        Assert.All(before, file => Assert.Contains(file, Files()));
        Assert.Equal(0, store.DeleteAttempts);
    }

    private sealed class RefuseApprovalAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_approved"))
            {
                Fired = true;
                throw new IOException("Synthetic approval audit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CollectConfirmedPreparation(PhotoReviewApprovalTests fixture, ApprovalStore store,
        IObjectStore inner, bool enabled) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!enabled || Fired || store.PutAttempts != 3)
            {
                return;
            }

            // Observe a confirmed preparation with no SQL lock left, before the approval's final transaction.
            await using var connection = new NpgsqlConnection(fixture._connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var mark = new NpgsqlCommand("""
                update media.review_promotions set cleanup_requested_at=$1
                where committed_at is null and writer_settled_at is not null and deleted_at is null
                returning generation
                """, connection);
            mark.Parameters.AddWithValue(fixture._time.GetUtcNow());
            var generation = await mark.ExecuteScalarAsync(cancellationToken);
            if (generation is not Guid expected)
            {
                return;
            }

            Fired = true;
            await using var source = NpgsqlDataSource.Create(fixture._connectionString);
            var gc = new ReviewPromotionCleanupDispatcher(source, inner, fixture._time, enabled: true);
            var claim = Assert.IsType<ReviewPromotionCleanupClaim>(await gc.ClaimAsync(cancellationToken));
            Assert.Equal(expected, claim.Generation);
            Assert.True(claim.WriterWasSettled);
            foreach (var kind in PhotoVariants.All)
            {
                await inner.DeleteAsync(ObjectKey.ForPromotedDerivative(expected, kind), cancellationToken);
            }

            Assert.True(await gc.CompleteSweepAsync(claim, cancellationToken));
        }
    }

    [Fact]
    public async Task ApprovalExpansionPreservesAnExistingRejectionItsReceiptAndReplay()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261022210000_PrivateReviewPromotions", cancellationToken: Ct);
        }

        _connectionString = database.AppConnectionString;
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var first = Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
            new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                PhotoReviewRejectionReason.DoesNotMeetPolicy, key, Ct));
        var previous = await DecisionHistoryAsync(item);
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.GetService<IMigrator>().MigrateAsync("20261022220000_PhotoReviewApprovals", cancellationToken: Ct);
        }

        Assert.Equal(previous, await DecisionHistoryAsync(item));
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        Assert.Equal(first, await new PhotoReviewRejections(Factory(), noStorage, _time).RejectAsync(reviewer,
            item.CaseId, 1, PhotoReviewRejectionReason.DoesNotMeetPolicy, key, Ct));
        Assert.IsType<PhotoReviewApprovalResult.KeyReused>(await ApproveAsync(reviewer, item, key, noStorage, version: 1));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_decision_commands
            where case_id=$1 and result_decision='reject' and result_asset_id is null
            """, item.CaseId));
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Empty(noStorage.Opened);
        var (nextReviewer, nextItem) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(nextReviewer, nextItem, Key()));
        Assert.Equal(previous, await DecisionHistoryAsync(item));
    }

    private async Task<string[]> DecisionHistoryAsync(CaseData item)
    {
        await using var db = Context();
        var decision = await db.Database.SqlQuery<string>($"""
            select to_jsonb(d)::text as "Value" from safety.photo_review_decisions d where case_id={item.CaseId}
            """).SingleAsync(Ct);
        var receipt = await db.Database.SqlQuery<string>($"""
            select (to_jsonb(c)-'result_decision'-'result_asset_id')::text as "Value"
            from safety.photo_review_decision_commands c where case_id={item.CaseId}
            """).SingleAsync(Ct);
        return [decision, receipt];
    }

    [Fact]
    public async Task ApprovalConstraintsRejectMismatchedReasonsRetentionAndReceiptAssets()
    {
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        foreach (var (decision, reason, hours) in new[]
        {
            ("approve", "does_not_meet_policy", 24),
            ("reject", "meets_policy", 168),
            ("approve", "meets_policy", 168),
            ("reject", "does_not_meet_policy", 24),
            ("unknown", "meets_policy", 24),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("""
                insert into safety.photo_review_decisions(id,case_id,case_version,actor_id,decision,reason_code,
                  candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until)
                select $2,case_id,99,actor_id,$3,$4,candidate_generation,policy_version,decided_at,
                  decided_at+($5::integer*interval '1 hour'),history_retain_until
                from safety.photo_review_decisions where case_id=$1
                """, item.CaseId, Guid.NewGuid(), decision, reason, hours));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        foreach (var (decision, asset) in new (string Decision, Guid? Asset)[]
        {
            ("approve", null), ("approve", Guid.Empty), ("reject", approved.AssetId), ("unknown", approved.AssetId),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("""
                insert into safety.photo_review_decision_commands(actor_id,operation,key_hash,case_id,intent_sha256,
                  result_version,result_photo_version,created_at,result_decision,result_asset_id)
                select actor_id,operation,$2,case_id,intent_sha256,result_version,result_photo_version,created_at,$3,$4::uuid
                from safety.photo_review_decision_commands where case_id=$1
                """, item.CaseId, RandomNumberGenerator.GetBytes(32), decision, (object?)asset ?? DBNull.Value));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApprovalReauthorizesAfterTheLowerOwnersGuardInBothPhases(bool finalPhase, bool revokeGrant)
    {
        var (reviewer, item) = await PreparedAsync(Guid.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff"),
            Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var candidateBefore = await CandidateMetadataAsync(item);
        _time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        NpgsqlTransaction? held = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var observation = new OwnerGuardObservation(item.OwnerId) { Enabled = !finalPhase };
        async Task HoldOwnerAsync()
        {
            held = await locker.BeginTransactionAsync(budget.Token);
            await using var command = new NpgsqlCommand("select 1 from safety.user_guards where user_id=$1 for update", locker, held);
            command.Parameters.AddWithValue(item.OwnerId);
            Assert.NotNull(await command.ExecuteScalarAsync(budget.Token));
        }

        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), afterPut: async key =>
        {
            if (finalPhase && key.Value.EndsWith("-full", StringComparison.Ordinal))
            {
                await HoldOwnerAsync();
                observation.Enabled = true;
            }
        });
        Task<PhotoReviewApprovalResult>? approval = null;
        var released = false;
        try
        {
            if (!finalPhase)
            {
                await HoldOwnerAsync();
            }
            approval = new PhotoReviewApprovals(Factory(observation), store, _time).ApproveAsync(reviewer,
                item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), budget.Token);
            var pid = await observation.Pid.Task.WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
            await WaitForOwnerGuardAsync(pid, locker.ProcessID, approval, budget.Token);
            if (revokeGrant)
            {
                // This must finish while the owner is held: preauthorization cannot retain the actor's guard.
                await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: false);
            }
            else
            {
                // Only TOTP expires. Session idle/absolute, review/candidate and final promotion remain live.
                _time.Advance(TimeSpan.FromSeconds(2));
            }
            Assert.False(approval.IsCompleted);
            await held!.CommitAsync(budget.Token);
            released = true;
            Assert.IsType<PhotoReviewApprovalResult.Forbidden>(await approval);
            Assert.Equal(finalPhase ? 3 : 0, store.PutAttempts);
            Assert.Equal(0, store.DeleteAttempts);
            await AssertNoApprovalAsync(item);
            Assert.Equal(candidateBefore, await CandidateMetadataAsync(item));
        }
        finally
        {
            try
            {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (held is not null && !released)
                {
                    await held.RollbackAsync(release.Token);
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        await budget.CancelAsync();
                    }
                    finally
                    {
                        if (approval is not null)
                        {
                            using var observationBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            try
                            {
                                await approval.WaitAsync(observationBudget.Token);
                            }
                            catch (OperationCanceledException) when (approval.IsCompleted && approval.IsCanceled && budget.IsCancellationRequested)
                            {
                            }
                        }
                    }
                }
                finally
                {
                    if (held is not null)
                    {
                        await held.DisposeAsync();
                    }
                }
            }
        }
    }

    private async Task WaitForOwnerGuardAsync(int pid, int blocker, Task operation, CancellationToken cancellationToken)
    {
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync(cancellationToken);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("select $2=any(pg_blocking_pids($1))", observer);
            command.Parameters.AddWithValue(pid);
            command.Parameters.AddWithValue(blocker);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }
            Assert.False(operation.IsCompleted, "Approval must actually wait on the held owner's lower guard.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        Assert.Fail("PostgreSQL did not observe approval blocked by the owner's guard.");
    }

    private sealed class OwnerGuardObservation(Guid owner) : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        internal TaskCompletionSource<int> Pid { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("from safety.user_guards", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == owner))
            {
                Pid.TrySetResult(Assert.IsType<NpgsqlConnection>(command.Connection).ProcessID);
            }
            return ValueTask.FromResult(result);
        }
    }

    private Task<PhotoReviewApprovalResult> ApproveAsync(PhotoReviewActor actor, CaseData item, IdempotencyKey key,
        IObjectStore? store = null, long version = 2) => new PhotoReviewApprovals(Factory(), store ?? new FileSystemObjectStore(_root.FullName), _time)
        .ApproveAsync(actor, item.CaseId, version, PhotoReviewApprovalReason.MeetsPolicy, key, Ct);

    private async Task AssertNoApprovalAsync(CaseData item)
    {
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decision_commands where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_type='photo_review_decision'"));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview' and version=3", item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1 and version=2", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_promotions where case_id=$1 and committed_at is not null", item.CaseId));
    }

    private string[] Files() => [.. Directory.GetFiles(_root.FullName, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(_root.FullName, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))];

    private sealed record VariantProof(string Kind, string ObjectKey, long SizeBytes, Guid Generation,
        long CandidateSize, long PromotionSize, long DecisionSize, byte[] CandidateSha256, byte[] PromotionSha256, byte[] DecisionSha256);

    private sealed class ApprovalStore(IObjectStore inner, Func<ObjectKey, Task>? beforePut = null,
        int failingPut = 0, bool refuseAll = false, Func<ObjectKey, Task>? afterPut = null) : IObjectStore
    {
        internal int PutAttempts { get; private set; }
        internal int DeleteAttempts { get; private set; }
        internal List<ObjectKey> Opened { get; } = [];

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            PutAttempts++;
            if (refuseAll)
            {
                throw new InvalidOperationException("This command must not access storage.");
            }

            Assert.Equal(StorageArea.Derivatives, key.Area);
            Assert.StartsWith("derivatives/review-", key.Value, StringComparison.Ordinal);
            if (beforePut is not null)
            {
                await beforePut(key);
            }

            if (PutAttempts == failingPut)
            {
                throw new IOException("Synthetic uncertain approval copy.");
            }

            await inner.PutAsync(key, content, length, contentType, cancellationToken);
            if (afterPut is not null)
            {
                await afterPut(key);
            }
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Opened.Add(key);
            if (refuseAll)
            {
                throw new InvalidOperationException("This command must not access storage.");
            }

            return inner.OpenReadAsync(key, cancellationToken);
        }

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            throw new InvalidOperationException("Approval records cleanup; it never deletes storage directly.");
        }
    }

    private sealed class ApprovalCopiesBarrier(CancellationToken cancellationToken)
    {
        internal ConcurrentDictionary<string, byte> Generations { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource BothReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task AfterPutAsync(ObjectKey key)
        {
            if (!key.Value.EndsWith("-full", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(Generations.TryAdd(key.Value[..^5], 0));
            if (Generations.Count == 2)
            {
                BothReady.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
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

    private sealed class ApprovalCommitFault(string connectionString, Guid uploadId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasAssetAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before approval commit.");
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
            if (await HasAssetAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost approval commit response.");
            }
        }

        private async Task<bool> HasAssetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from media.media_assets where upload_id=$1)", connection, transaction);
            command.Parameters.AddWithValue(uploadId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private async Task<(PhotoReviewActor Reviewer, CaseData Item)> PreparedAsync(Guid? reviewerId = null, Guid? ownerId = null)
    {
        var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ActorAsync(AccountRoles.Moderator, reviewerId);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: true);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: true);
        var item = await CaseAsync(ownerId);
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
            values({user},'Active',{now},true,false,true,true,0,'synthetic-approval-stamp')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.user_roles(user_id,role_id) select {user},id from identity.roles where name={role}
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.login_sessions(id,user_id,key_hash,ticket,security_stamp,description,policy,
              created_at,last_seen_at,idle_expires_at,absolute_expires_at,reauthenticated_at,mfa_verified_at,totp_verified_at)
            values({session},{user},{hash},{ticket},'synthetic-approval-stamp','synthetic',{policy},
              {now.AddMinutes(-1)},{now},{now.AddMinutes(10)},{now.AddHours(1)},{now},{now},{now})
            """, Ct);
        await transaction.CommitAsync(Ct);
        return new PhotoReviewActor(user, session);
    }

    private async Task GrantAsync(Guid userId, PhotoReviewCapability capability, bool enabled)
    {
        await using var db = Context();
        Assert.True(OperatorName.TryParse("approval-test", out var operatorName));
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

    private async Task<long> ScalarAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 2 };
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CaseData(Guid CaseId, Guid OwnerId, Guid UploadId, long UploadVersion,
        DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline);

    private sealed class ContextFactory(DbContextOptions<DuoraDbContext> options) : IDbContextFactory<DuoraDbContext>
    {
        public DuoraDbContext CreateDbContext() => new(options);
    }
}
