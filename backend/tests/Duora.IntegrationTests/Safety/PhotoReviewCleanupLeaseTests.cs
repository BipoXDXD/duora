using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData("approved", false, true)]
    [InlineData("approved", true, true)]
    [InlineData("abandoned", false, true)]
    [InlineData("abandoned", true, true)]
    [InlineData("abandoned", false, false)]
    [InlineData("abandoned", true, false)]
    public async Task ExistingCandidateCollectorsCannotAcknowledgeOrRetryALeaseThatExpiredDuringALockWait(
        string collector, bool retry, bool writerSettled)
    {
        var label = "candidate-existing-lease-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        Guid generation;
        Func<CancellationToken, Task<bool>> finalize;
        Func<CancellationToken, Task<int>> dispatch;
        if (collector == "approved")
        {
            var (reviewer, item) = await PreparedAsync();
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
            generation = await CleanupGenerationAsync(item);
            var gc = ApprovedCleanup(source);
            var claim = Assert.IsType<DecidedCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }
        else
        {
            generation = await SeedAbandonedLeaseCandidateAsync(writerSettled);
            var gc = new ReviewCandidateCleanupDispatcher(source, new FileSystemObjectStore(_root.FullName), _time,
                enabled: true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
            var claim = Assert.IsType<ReviewCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
            Assert.Equal(writerSettled, claim.WriterWasSettled);
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }

        _time.Advance(TimeSpan.FromSeconds(59));
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        var retained = files.Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();

        Assert.False(await AfterCleanupCandidateWaitAsync(generation, label, finalize,
            () => _time.Advance(TimeSpan.FromSeconds(2))));

        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(1, await dispatch(Ct));
        Assert.Equal(retained, Files());
        // A remoção física grava deleted_at no relógio do banco, não no relógio falso (dias atrás): é dele que o
        // expurgo de tombstones conta a janela de backup (ADR-0035, P3).
        Assert.Equal(writerSettled ? 1L : 0L, await ScalarAsync(
            "select count(*) from media.review_candidates where generation=$1 and deleted_at between statement_timestamp() - interval '5 minutes' and statement_timestamp()", generation));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and cleanup_token is null
              and cleanup_lease_until is null and last_cleanup_at is not null
            """, generation));
    }

    private async Task<Guid> SeedAbandonedLeaseCandidateAsync(bool writerSettled)
    {
        var owner = await ActorAsync(AccountRoles.Participant);
        var upload = Upload.Reserve(owner.UserId, Key(), PhotoFormat.Jpeg, 3, _time.GetUtcNow());
        await using (var db = Context())
        {
            db.Add(upload);
            await db.SaveChangesAsync(Ct);
        }

        var generation = Guid.NewGuid();
        var now = _time.GetUtcNow();
        byte[] partialBytes = [1, 2, 3];
        await ExecuteAsync("update media.uploads set state='Processing',version=2,content_sha256=$2 where id=$1",
            upload.Id, SHA256.HashData(partialBytes));
        await ExecuteAsync("""
            insert into media.review_candidates(generation,upload_id,upload_version,detected_type,width,height,
              created_at,prepare_deadline,cleanup_requested_at,writer_settled_at,cleanup_available_at)
            values($1,$2,2,'image/jpeg',1,1,$3,$4,$3,case when $5 then $3 else null end,$3)
            """, generation, upload.Id, now, now.AddMinutes(1), writerSettled);
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            var key = ObjectKey.ForReviewCandidate(generation, kind);
            await ExecuteAsync("""
                insert into media.review_candidate_variants(generation,kind,object_key,width,height,size_bytes,sha256)
                values($1,$2,$3,1,1,$4,$5)
                """, generation, PhotoVariants.Name(kind), key.Value, partialBytes.LongLength, SHA256.HashData(partialBytes));
            // Arbitrary partial private bytes model abandoned writes; no sanitization result is asserted here.
            using var content = new MemoryStream(partialBytes);
            await store.PutAsync(key, content, partialBytes.Length, "image/webp", Ct);
        }

        return generation;
    }

    [Theory]
    [InlineData("original", false, true)]
    [InlineData("original", true, true)]
    [InlineData("promotion", false, true)]
    [InlineData("promotion", true, true)]
    [InlineData("promotion", false, false)]
    [InlineData("promotion", true, false)]
    public async Task ExistingOriginalAndPromotionCollectorsCannotFinalizeAnExpiredLeaseAfterWaiting(
        string collector, bool retry, bool writerSettled)
    {
        var label = "other-cleanup-lease-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var store = new FileSystemObjectStore(_root.FullName);
        Guid rowId;
        string lockSql;
        string prefix;
        Func<CancellationToken, Task<bool>> finalize;
        Func<CancellationToken, Task<int>> dispatch;
        if (collector == "original")
        {
            var item = await CaseAsync();
            await using var db = Context();
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await OriginalCleanupQueue.EnqueueAsync(db, item.UploadId, _time.GetUtcNow(), Ct);
                await transaction.CommitAsync(Ct);
            }

            var key = await db.Database.SqlQuery<string>($"""
                select quarantine_key as "Value" from media.uploads where id={item.UploadId}
                """).SingleAsync(Ct);
            var original = TestImages.Jpeg(64, 32);
            using (var content = new MemoryStream(original))
            {
                await store.PutAsync(ObjectKey.Parse(key), content, original.Length, "image/jpeg", Ct);
            }

            var gc = new OriginalCleanupDispatcher(source, store, _time, enabled: true,
                NullLogger<OriginalCleanupDispatcher>.Instance);
            var claim = Assert.IsType<OriginalCleanupClaim>(await gc.ClaimAsync(Ct));
            rowId = item.UploadId;
            prefix = key + ":";
            lockSql = "select upload_id from media.original_cleanup where upload_id=$1 for update";
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }
        else
        {
            var (reviewer, item) = await PreparedAsync();
            if (writerSettled)
            {
                var fault = new RefuseApprovalAudit();
                var service = new PhotoReviewApprovals(Factory(fault), store, _time);
                await Assert.ThrowsAsync<IOException>(() => service.ApproveAsync(reviewer, item.CaseId, 2,
                    PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct));
                Assert.True(fault.Fired);
            }
            else
            {
                Assert.IsType<PhotoReviewApprovalResult.Unavailable>(await ApproveAsync(reviewer, item, Key(),
                    new ApprovalStore(store, failingPut: 2)));
            }

            await using var db = Context();
            rowId = await db.Database.SqlQuery<Guid>($"""
                select generation as "Value" from media.review_promotions where case_id={item.CaseId}
                """).SingleAsync(Ct);
            var gc = new ReviewPromotionCleanupDispatcher(source, store, _time, enabled: true);
            var claim = Assert.IsType<ReviewPromotionCleanupClaim>(await gc.ClaimAsync(Ct));
            Assert.Equal(writerSettled, claim.WriterWasSettled);
            prefix = $"derivatives/review-{rowId:N}-";
            lockSql = "select generation from media.review_promotions where generation=$1 for update";
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }

        _time.Advance(TimeSpan.FromSeconds(59));
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        var retained = files.Where(file => !file.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.True(retained.Length < files.Length, "The claimed generation must have real bytes before cleanup.");

        Assert.False(await AfterOtherCleanupRowWaitAsync(rowId, lockSql, label, finalize));

        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(1, await dispatch(Ct));
        Assert.Equal(retained, Files());
        if (collector == "original")
        {
            Assert.Equal(0, await ScalarAsync("select count(*) from media.original_cleanup where upload_id=$1", rowId));
        }
        else
        {
            // A remoção física grava deleted_at no relógio do banco, não no relógio falso (dias atrás): é dele que o
            // expurgo de tombstones conta a janela de backup (ADR-0035, P3).
            Assert.Equal(writerSettled ? 1L : 0L, await ScalarAsync(
                "select count(*) from media.review_promotions where generation=$1 and deleted_at between statement_timestamp() - interval '5 minutes' and statement_timestamp()", rowId));
            Assert.Equal(1, await ScalarAsync("""
                select count(*) from media.review_promotions where generation=$1 and cleanup_token is null
                  and cleanup_lease_until is null and last_cleanup_at is not null
                """, rowId));
        }
    }

    private async Task<string> AllCleanupProjectionAsync()
    {
        var candidates = await ExpirationProjectionAsync();
        await using var db = Context();
        var other = await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.upload_id),'[]'::jsonb) from media.original_cleanup c),
              (select coalesce(jsonb_agg(to_jsonb(p) order by p.generation),'[]'::jsonb) from media.review_promotions p),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.generation,v.kind),'[]'::jsonb) from media.review_promotion_variants v)
            )::text as "Value"
            """).SingleAsync(Ct);
        return candidates + "\n" + other;
    }

    private async Task<bool> AfterOtherCleanupRowWaitAsync(Guid rowId, string lockSql, string label,
        Func<CancellationToken, Task<bool>> start)
    {
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand(lockSql, locker, held);
        hold.Parameters.AddWithValue(rowId);
        Assert.Equal(rowId, await hold.ExecuteScalarAsync(Ct));
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var operation = start(attempt.Token);
        var released = false;
        try
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            observation.CancelAfter(TimeSpan.FromSeconds(5));
            var waiting = false;
            for (var count = 0; count < 100 && !operation.IsCompleted; count++)
            {
                waiting = await ScalarAsync("""
                    select count(*) from pg_stat_activity where application_name=$1 and $2=any(pg_blocking_pids(pid))
                    """, label, locker.ProcessID) == 1;
                if (waiting)
                {
                    break;
                }

                await Task.Delay(10, observation.Token);
            }

            Assert.True(waiting, "Cleanup must wait for its actual obligation row before the clock crosses its lease.");
            _time.Advance(TimeSpan.FromSeconds(2));
            await held.CommitAsync(Ct);
            released = true;
            return await operation.WaitAsync(TimeSpan.FromSeconds(5), Ct);
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
                    await operation;
                }
                catch (OperationCanceledException) when (operation.IsCompleted && operation.IsCanceled && attempt.IsCancellationRequested)
                {
                }
            }
        }
    }
}
