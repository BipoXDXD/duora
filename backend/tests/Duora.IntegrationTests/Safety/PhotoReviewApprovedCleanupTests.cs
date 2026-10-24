using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task ApprovedCleanupRemovesOnlyItsPrivateGenerationAndPreservesHistoryFinalBytesAndReplay()
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key));
        var generation = await CleanupGenerationAsync(item);
        var before = Files();
        var retained = before.Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, before.Length - retained.Length);
        var history = await CleanupHistoryAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var gc = ApprovedCleanup(source);

        Assert.Equal(1, await gc.DispatchOnceAsync(Ct));

        Assert.Equal(retained, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and committed_at is not null
              and deleted_at is not null and cleanup_requested_at is not null and cleanup_token is null
              and cleanup_lease_until is null and last_cleanup_at=deleted_at
            """, generation));
        Assert.Equal(3, await ScalarAsync("select count(*) from media.media_variants where media_asset_id=$1", approved.AssetId));
        Assert.Equal(0, await gc.DispatchOnceAsync(Ct));
        Assert.Equal(approved, Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key,
            new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedCleanupCannotEnrollPendingOrRejectedCandidatesEvenWithACleanupMarker(bool rejected)
    {
        var (reviewer, item) = await PreparedAsync();
        if (rejected)
        {
            Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
                new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        }

        var generation = await CleanupGenerationAsync(item);
        await ExecuteAsync("update media.review_candidates set cleanup_requested_at=$2 where generation=$1", generation, _time.GetUtcNow());
        var before = Files();
        var history = await CleanupHistoryAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);

        Assert.Equal(0, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(before, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
    }

    [Fact]
    public async Task ApprovedCleanupPreservesAnotherConfirmedCandidateAndLeavesTheAbandonedCollectorUnchanged()
    {
        var (reviewer, approvedItem) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, approvedItem, Key()));
        var awaitingItem = await CaseAsync();
        var approvedGeneration = await CleanupGenerationAsync(approvedItem);
        var awaitingGeneration = await CleanupGenerationAsync(awaitingItem);
        var before = Files();
        await using var source = NpgsqlDataSource.Create(_connectionString);

        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));

        Assert.Equal(before.Where(file => !file.StartsWith($"review-candidates/{approvedGeneration:N}-", StringComparison.Ordinal)), Files());
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and cleanup_requested_at is null and cleanup_token is null
            """, awaitingGeneration));
        // The existing abandoned-preparation collector remains unable to claim either confirmed generation.
        var abandoned = new ReviewCandidateCleanupDispatcher(source, new FileSystemObjectStore(_root.FullName), _time,
            enabled: true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
        Assert.Null(await abandoned.ClaimAsync(Ct));
    }

    [Fact]
    public async Task ApprovedCleanupRetriesPartialStorageFailureAfterDurableBackoff()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var generation = await CleanupGenerationAsync(item);
        var retained = Files().Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        var failure = true;
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), (key, _) =>
        {
            if (failure && key.Value.EndsWith("-card", StringComparison.Ordinal))
            {
                failure = false;
                throw new IOException("Synthetic candidate DELETE failure.");
            }

            return Task.CompletedTask;
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var gc = ApprovedCleanup(source, store);

        Assert.Equal(1, await gc.DispatchOnceAsync(Ct));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and cleanup_token is null and cleanup_available_at>$2 and cleanup_attempts=1
            """, generation, _time.GetUtcNow()));
        Assert.Equal(0, await gc.DispatchOnceAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(retained, Files());
    }

    [Fact]
    public async Task ApprovedCleanupLeaseExpiryAllowsRecoveryAndRejectsThePreviousToken()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var gc = ApprovedCleanup(source);
        var first = Assert.IsType<ApprovedCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
        Assert.Null(await ApprovedCleanup(source).ClaimAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(61));
        // Expiration must deny ACK even before another collector changes the token.
        Assert.False(await gc.CompleteSweepAsync(first, Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", first.Generation));
        var recovered = Assert.IsType<ApprovedCandidateCleanupClaim>(await ApprovedCleanup(source).ClaimAsync(Ct));
        Assert.Equal(first.Generation, recovered.Generation);
        Assert.NotEqual(first.Token, recovered.Token);
        Assert.False(await gc.CompleteSweepAsync(first, Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", first.Generation));
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            await store.DeleteAsync(ObjectKey.ForReviewCandidate(recovered.Generation, kind), Ct);
        }

        Assert.True(await ApprovedCleanup(source).CompleteSweepAsync(recovered, Ct));
        Assert.False(await gc.CompleteSweepAsync(first, Ct));
    }

    [Fact]
    public async Task ApprovedCleanupShutdownLeavesARecoverableObligationWithoutATerminalAcknowledgement()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var generation = await CleanupGenerationAsync(item);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApprovedCleanup(source, store).DispatchOnceAsync(shutdown.Token));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and cleanup_token is not null and cleanup_requested_at is not null
            """, generation));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedCleanupRequiresAnExplicitDueMarkerAndRespectsTheDisabledSwitch(bool disabled)
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var generation = await CleanupGenerationAsync(item);
        if (!disabled)
        {
            await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where generation=$1",
                generation, _time.GetUtcNow().AddMinutes(1));
        }

        var before = Files();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ApprovedCleanup(source, enabled: !disabled).DispatchOnceAsync(Ct));
        Assert.Equal(before, Files());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and cleanup_token is not null", generation));
    }

    [Theory]
    [InlineData("no-marker")]
    [InlineData("future-marker")]
    [InlineData("retention")]
    [InlineData("policy")]
    [InlineData("case")]
    public async Task ApprovedStateAloneCannotAuthorizeCleanupWithoutTheExactDecisionObligation(string mismatch)
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var generation = await CleanupGenerationAsync(item);
        switch (mismatch)
        {
            case "no-marker":
                await ExecuteAsync("update media.review_candidates set cleanup_requested_at=null where generation=$1", generation);
                break;
            case "future-marker":
                await ExecuteAsync("update media.review_candidates set cleanup_requested_at=$2 where generation=$1",
                    generation, _time.GetUtcNow().AddMinutes(1));
                break;
            case "retention":
                await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 second' where generation=$1", generation);
                break;
            case "policy":
                await ExecuteAsync("update safety.photo_review_cases set policy_version='different-policy' where id=$1", item.CaseId);
                break;
            case "case":
                var other = await CaseAsync();
                await ExecuteAsync("update media.review_candidates set case_id=$2 where generation=$1", generation, other.CaseId);
                break;
        }

        var before = Files();
        var history = await CleanupHistoryAsync();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(before, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and (cleanup_token is not null or deleted_at is not null)", generation));
    }

    [Fact]
    public async Task ApprovedCleanupReleasesItsSqlLockBeforeStartingStorageDeletion()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var generation = await CleanupGenerationAsync(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var sweep = ApprovedCleanup(source, store).DispatchOnceAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand("select generation from media.review_candidates where generation=$1 for update nowait",
                connection, transaction)
            { CommandTimeout = 2 };
            command.Parameters.AddWithValue(generation);
            Assert.Equal(generation, await command.ExecuteScalarAsync(Ct));
            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            release.TrySetResult();
            await sweep.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }

        Assert.Equal(1, await sweep);
    }

    private ApprovedCandidateCleanupDispatcher ApprovedCleanup(NpgsqlDataSource source, IObjectStore? store = null, bool enabled = true) =>
        new(source, store ?? new FileSystemObjectStore(_root.FullName), _time, enabled,
            NullLogger<ApprovedCandidateCleanupDispatcher>.Instance);

    private async Task<Guid> CleanupGenerationAsync(CaseData item)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<Guid>($"""
            select candidate_generation as "Value" from safety.photo_review_cases where id={item.CaseId}
            """).SingleAsync(Ct);
    }

    private async Task<string> CleanupHistoryAsync()
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(d) order by d.id),'[]'::jsonb) from safety.photo_review_decisions d),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.decision_id,v.variant),'[]'::jsonb) from safety.photo_review_decision_variants v),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.actor_id,c.operation,c.key_hash),'[]'::jsonb) from safety.photo_review_decision_commands c),
              (select coalesce(jsonb_agg(to_jsonb(e) order by e.id),'[]'::jsonb) from ops.audit_events e)
            )::text as "Value"
            """).SingleAsync(Ct);
    }

    private sealed class ApprovedCleanupStore(IObjectStore inner, Func<ObjectKey, CancellationToken, Task> beforeDelete) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            await beforeDelete(key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await inner.DeleteAsync(key, cancellationToken);
        }
    }
}
