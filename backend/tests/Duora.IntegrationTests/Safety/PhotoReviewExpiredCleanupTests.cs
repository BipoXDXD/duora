using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task ExpiredCleanupDeletesOnlyItsPrivateGenerationAndKeepsTheSystemProofAndOtherBytes(int daysLate)
    {
        var (reviewer, approvedItem) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, approvedItem, Key()));
        var (item, generation) = await ExpiredCleanupCaseAsync();
        _time.Advance(TimeSpan.FromDays(daysLate));
        byte[] unrelatedOriginal = [11, 22, 33];
        using (var content = new MemoryStream(unrelatedOriginal))
        {
            await new FileSystemObjectStore(_root.FullName).PutAsync(ObjectKey.NewQuarantine(), content,
                unrelatedOriginal.Length, "application/octet-stream", Ct);
        }

        var before = Files();
        var retained = before.Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, before.Length - retained.Length);
        var history = await CleanupHistoryAsync();
        var domain = await ExpiredCleanupDomainAsync();
        var proof = await ScalarAsync("select cleanup_audit_id from media.review_candidates where generation=$1", generation);
        await using var source = NpgsqlDataSource.Create(_connectionString);

        Assert.Equal(1, await ExpiredCleanup(source).DispatchOnceAsync(Ct));

        Assert.Equal(retained, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(domain, await ExpiredCleanupDomainAsync());
        Assert.Equal(3, await ScalarAsync("select count(*) from media.media_variants where media_asset_id=$1", approved.AssetId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join ops.audit_events e on e.id=c.cleanup_audit_id
            where c.generation=$1 and c.cleanup_audit_id=$2 and c.deleted_at is not null
              and c.cleanup_token is null and c.cleanup_lease_until is null and c.last_cleanup_at=c.deleted_at
              and c.committed_at is not null and c.writer_settled_at is not null
              and e.action='safety.photo_review_expired' and e.actor_kind='System' and e.target_id=$3
            """, generation, proof, item.CaseId));
        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task ExpiredCleanupCannotEnrollOtherReviewOutcomesWithoutAnExpirationProof(string state)
    {
        var (reviewer, item) = await PreparedAsync();
        if (state == "approved")
        {
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        }
        else if (state == "rejected")
        {
            Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
                new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        }

        var generation = await CleanupGenerationAsync(item);
        await ExecuteAsync("update media.review_candidates set cleanup_requested_at=coalesce(cleanup_requested_at,$2),cleanup_available_at=$2 where generation=$1",
            generation, _time.GetUtcNow());
        var before = await ExpirationProjectionAsync();
        var files = Files();
        await using var source = NpgsqlDataSource.Create(_connectionString);

        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(files, Files());
        Assert.Equal(before, await ExpirationProjectionAsync());
    }

    [Theory]
    [InlineData("wrong-action")]
    [InlineData("wrong-case")]
    [InlineData("wrong-version")]
    [InlineData("wrong-generation")]
    [InlineData("no-marker-or-proof")]
    [InlineData("no-proof")]
    [InlineData("future-marker")]
    public async Task ExpiredCleanupRequiresItsExactImmutableSystemFact(string mismatch)
    {
        var (item, generation) = await ExpiredCleanupCaseAsync();
        if (mismatch == "wrong-action")
        {
            await ExecuteAsync("""
                insert into ops.audit_events (occurred_at,actor_kind,action,target_type,target_id,target_version)
                select $2,'System','safety.photo_review_requested','photo_review_case',id,version
                from safety.photo_review_cases where id=$1
                """, item.CaseId, _time.GetUtcNow());
            await ExecuteAsync("""
                update media.review_candidates set cleanup_audit_id=(select id from ops.audit_events
                  where action='safety.photo_review_requested' and target_id=$2 and actor_kind='System'
                    and target_version=(select version from safety.photo_review_cases where id=$2)) where generation=$1
                """, generation, item.CaseId);
        }
        else if (mismatch == "wrong-case")
        {
            var other = await CaseAsync();
            _time.Advance(ReviewWait + TimeSpan.FromHours(1));
            Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
            await ExecuteAsync("""
                update media.review_candidates set cleanup_audit_id=(select cleanup_audit_id from media.review_candidates
                  where case_id=$2) where generation=$1
                """, generation, other.CaseId);
            // Keep the second, valid obligation outside this invocation's due window.
            await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where case_id=$1", other.CaseId, _time.GetUtcNow().AddMinutes(1));
        }
        else if (mismatch == "wrong-version")
        {
            await ExecuteAsync("update safety.photo_review_cases set version=version+1 where id=$1", item.CaseId);
        }
        else if (mismatch == "wrong-generation")
        {
            await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", item.CaseId, Guid.NewGuid());
        }
        else if (mismatch == "no-marker-or-proof")
        {
            await ExecuteAsync("update media.review_candidates set cleanup_audit_id=null,cleanup_requested_at=null where generation=$1", generation);
        }
        else if (mismatch == "no-proof")
        {
            await ExecuteAsync("update media.review_candidates set cleanup_audit_id=null where generation=$1", generation);
        }
        else
        {
            await ExecuteAsync("update media.review_candidates set cleanup_requested_at=$2 where generation=$1", generation, _time.GetUtcNow().AddMinutes(1));
        }

        var files = Files();
        var before = await ExpirationProjectionAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(files, Files());
        Assert.Equal(before, await ExpirationProjectionAsync());
    }

    [Fact]
    public async Task ExpiredCleanupRecoversAnExpiredLeaseWithoutAcceptingAnOldTokenOrAnExpiredAck()
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var gc = ExpiredCleanup(source);
        var old = Assert.IsType<ExpiredCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
        Assert.Null(await ExpiredCleanup(source).ClaimAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(await gc.CompleteSweepAsync(old, Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
        var current = Assert.IsType<ExpiredCandidateCleanupClaim>(await ExpiredCleanup(source).ClaimAsync(Ct));
        Assert.Equal(old.Generation, current.Generation);
        Assert.NotEqual(old.Token, current.Token);
        Assert.False(await gc.CompleteSweepAsync(old, Ct));
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            await store.DeleteAsync(ObjectKey.ForReviewCandidate(generation, kind), Ct);
        }

        Assert.True(await ExpiredCleanup(source).CompleteSweepAsync(current, Ct));
        Assert.False(await gc.CompleteSweepAsync(old, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredCleanupReadsItsAckAndRetryClockAfterWaitingForTheCandidateLock(bool retry)
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        var label = "expired-cleanup-lease-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var gc = ExpiredCleanup(source);
        var claim = Assert.IsType<ExpiredCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(59));
        var before = await ExpirationProjectionAsync();
        var files = Files();

        Assert.False(await AfterCleanupCandidateWaitAsync(generation, label,
            ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct),
            () => _time.Advance(TimeSpan.FromSeconds(2))));

        Assert.Equal(before, await ExpirationProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(1, await gc.DispatchOnceAsync(Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
    }

    [Fact]
    public async Task ExpiredCleanupSkipsALockedObligationAndClaimsAnotherGeneration()
    {
        var (_, locked) = await ExpiredCleanupCaseAsync();
        var (_, available) = await ExpiredCleanupCaseAsync();
        await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where generation=$1", locked, _time.GetUtcNow().AddMinutes(-1));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select generation from media.review_candidates where generation=$1 for update", connection, transaction);
        hold.Parameters.AddWithValue(locked);
        Assert.Equal(locked, await hold.ExecuteScalarAsync(Ct));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(5));
        ExpiredCandidateCleanupClaim claim;
        try
        {
            claim = Assert.IsType<ExpiredCandidateCleanupClaim>(await ExpiredCleanup(source).ClaimAsync(attempt.Token));
            Assert.Equal(available, claim.Generation);
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }

        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            await store.DeleteAsync(ObjectKey.ForReviewCandidate(available, kind), Ct);
        }

        Assert.True(await ExpiredCleanup(source).CompleteSweepAsync(claim, Ct));
        Assert.Equal(3, Files().Count(file => file.StartsWith($"review-candidates/{locked:N}-", StringComparison.Ordinal)));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and cleanup_token is null and deleted_at is null", locked));
    }

    [Fact]
    public async Task ExpiredCleanupCannotAcknowledgeAChangedContextEvenWithItsCurrentLiveToken()
    {
        var (item, generation) = await ExpiredCleanupCaseAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var claim = Assert.IsType<ExpiredCandidateCleanupClaim>(await ExpiredCleanup(source).ClaimAsync(Ct));
        await ExecuteAsync("update safety.photo_review_cases set version=version+1 where id=$1", item.CaseId);
        var before = await ExpirationProjectionAsync();
        var files = Files();

        Assert.False(await ExpiredCleanup(source).CompleteSweepAsync(claim, Ct));

        Assert.Equal(files, Files());
        Assert.Equal(before, await ExpirationProjectionAsync());
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is null", generation));
    }

    [Fact]
    public async Task ExpiredCleanupRetainsItsObligationAfterAPartialDeleteAndRetriesAfterBackoff()
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        var retained = Files().Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        var history = await CleanupHistoryAsync();
        var fail = true;
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), (key, _) =>
        {
            if (fail && key.Value.EndsWith("-card", StringComparison.Ordinal))
            {
                fail = false;
                throw new IOException("Synthetic expired candidate DELETE failure.");
            }

            return Task.CompletedTask;
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await ExpiredCleanup(source, store).DispatchOnceAsync(Ct));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and cleanup_token is null and cleanup_available_at>$2 and cleanup_audit_id is not null
            """, generation, _time.GetUtcNow()));
        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(retained, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
    }

    [Fact]
    public async Task ExpiredCleanupShutdownDoesNotAcknowledgeUnfinishedDeletion()
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
        {
            attempt.Cancel();
            return Task.CompletedTask;
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExpiredCleanup(source, store).DispatchOnceAsync(attempt.Token));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and cleanup_token is not null and cleanup_audit_id is not null
            """, generation));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
    }

    [Fact]
    public async Task ExpiredCleanupDoesNotHoldSqlLocksWhileDeletingBytes()
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), async (_, ct) =>
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var probe = new NpgsqlCommand("select generation from media.review_candidates where generation=$1 for update nowait", connection, transaction);
            probe.Parameters.AddWithValue(generation);
            Assert.Equal(generation, await probe.ExecuteScalarAsync(ct));
            await transaction.RollbackAsync(ct);
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await ExpiredCleanup(source, store).DispatchOnceAsync(Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
        Assert.DoesNotContain(Files(), file => file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredCleanupRespectsDueTimeAndTheDisabledSwitch(bool disabled)
    {
        var (_, generation) = await ExpiredCleanupCaseAsync();
        if (!disabled)
        {
            await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where generation=$1", generation, _time.GetUtcNow().AddMinutes(1));
        }

        var files = Files();
        var before = await ExpirationProjectionAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ExpiredCleanup(source, enabled: !disabled).DispatchOnceAsync(Ct));
        Assert.Equal(files, Files());
        Assert.Equal(before, await ExpirationProjectionAsync());
    }

    private async Task<(CaseData Item, Guid Generation)> ExpiredCleanupCaseAsync()
    {
        var item = await CaseAsync();
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));
        var advance = new AdvanceExpirationAuditClock(_time);
        Assert.Equal(1, await new PhotoReviewExpirations(Factory(advance), _time).ExpireOnceAsync(Ct));
        Assert.True(advance.Fired);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join ops.audit_events e on e.id=c.cleanup_audit_id
            where c.case_id=$1 and e.occurred_at>c.cleanup_requested_at
            """, item.CaseId));
        return (item, await CleanupGenerationAsync(item));
    }

    private async Task<string> ExpiredCleanupDomainAsync()
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(u) order by u.id),'[]'::jsonb) from media.uploads u),
              (select coalesce(jsonb_agg(to_jsonb(r) order by r.id),'[]'::jsonb) from safety.photo_review_cases r),
              (select coalesce(jsonb_agg(to_jsonb(a) order by a.id),'[]'::jsonb) from media.media_assets a),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.media_asset_id,v.kind),'[]'::jsonb) from media.media_variants v)
            )::text as "Value"
            """).SingleAsync(Ct);
    }

    private async Task<bool> AfterCleanupCandidateWaitAsync(Guid generation, string label,
        Func<CancellationToken, Task<bool>> start, Action whileWaiting)
    {
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select generation from media.review_candidates where generation=$1 for update", locker, held);
        hold.Parameters.AddWithValue(generation);
        Assert.Equal(generation, await hold.ExecuteScalarAsync(Ct));
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

            Assert.True(waiting, "Cleanup finalization must actually wait for the held candidate row.");
            whileWaiting();
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

    private ExpiredCandidateCleanupDispatcher ExpiredCleanup(NpgsqlDataSource source, IObjectStore? store = null, bool enabled = true) =>
        new(source, store ?? new FileSystemObjectStore(_root.FullName), _time, enabled,
            NullLogger<ExpiredCandidateCleanupDispatcher>.Instance);
}
