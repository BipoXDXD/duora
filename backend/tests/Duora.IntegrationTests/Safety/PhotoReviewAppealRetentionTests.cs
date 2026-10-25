using System.Data.Common;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealClosedOneTickBeforeMaximumClampsRetentionAndCleanupKeepsFinalDerivatives(bool approve)
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil.AddTicks(-1));
        var reviewer = await FreshAppealAssignerSessionAsync(data.Reviewer.UserId);
        var original = await OriginalAppealFactAsync(data.Data.DecisionId);
        var result = Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            reviewer, data.Appeal.AppealId, 2, approve, Key(), Ct));
        var generation = await CleanupGenerationAsync(data.Data.Item);
        var files = Files();
        var retained = files.Where(f => !f.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, files.Length - retained.Length);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_decisions d join safety.photo_review_appeals a on a.id=d.appeal_id
            where a.id=$1 and d.candidate_retention_until_snapshot=a.maximum_retention_until
              and d.decided_at<a.maximum_retention_until and a.closed_at=d.decided_at
            """, data.Appeal.AppealId));
        if (approve)
        {
            var catalog = await Catalog().CaptureAsync(10, 100_000_000, Ct);
            var asset = Assert.Single(catalog.Assets);
            Assert.Equal(result.AssetId, asset.AssetId);
            Assert.All(asset.Variants, v => Assert.NotNull(v.ExpectedSha256));
        }
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await AppealCleanup(source).DispatchOnceAsync(Ct));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(0, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(0, await UnavailableCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(1, await AppealCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(retained, Files());
        Assert.Equal(original, await OriginalAppealFactAsync(data.Data.DecisionId));
        Assert.Equal(approve ? 3 : 0, await ScalarAsync("select count(*) from media.media_variants where media_asset_id=$1", result.AssetId ?? Guid.Empty));
        Assert.Equal(0, await AppealCleanup(source).DispatchOnceAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemUnavailableAppealKeepsOriginalRejectionAndEnqueuesOnlyItsCandidate(bool expired)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var generation = await CleanupGenerationAsync(data.Data.Item);
        var original = await OriginalAppealFactAsync(data.Data.DecisionId);
        if (expired)
        {
            _time.SetUtcNow(data.Appeal.MaximumRetentionUntil.AddDays(3));
        }
        else
        {
            await new FileSystemObjectStore(_root.FullName).DeleteAsync(ObjectKey.ForReviewCandidate(generation, PhotoVariant.Full), Ct);
        }
        var files = Files();
        var retained = files.Where(f => !f.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        var service = new PhotoReviewAppealAvailability(Factory(), new FileSystemObjectStore(_root.FullName), _time);
        Assert.Equal(1, await service.ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.Equal(0, await service.ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_appeals a join media.review_candidates c on c.generation=a.candidate_generation
            join ops.audit_events e on e.id=c.cleanup_audit_id
            where a.id=$1 and a.status='ClosedUnavailable' and a.closed_at=$2
              and c.retention_until=least(a.closed_at,a.maximum_retention_until)
              and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_appeal_unavailable'
              and e.target_id=a.id and e.target_version=a.version
            """, data.Appeal.AppealId, _time.GetUtcNow()));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await AppealCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(retained, Files());
        Assert.Equal(original, await OriginalAppealFactAsync(data.Data.DecisionId));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("version")]
    [InlineData("retention")]
    public async Task AppealCleanupRefusesAnAlteredObligationWithoutTouchingBytes(string mismatch)
    {
        var data = await AssignedAppealForEvidenceAsync();
        Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, false, Key(), Ct));
        var generation = await CleanupGenerationAsync(data.Data.Item);
        var sql = mismatch switch
        {
            "audit" => "update media.review_candidates set cleanup_audit_id=1 where generation=$1",
            "version" => "update safety.photo_review_appeals set version=version+1 where candidate_generation=$1",
            _ => "update media.review_candidates set retention_until=retention_until+interval '1 hour' where generation=$1",
        };
        if (mismatch == "audit")
        {
            // A real, unrelated audit identity satisfies the FK but cannot authorize this cleanup.
            var audit = await ScalarAsync("select min(id) from ops.audit_events");
            await ExecuteAsync("update media.review_candidates set cleanup_audit_id=$2 where generation=$1", generation, audit);
        }
        else
        {
            await ExecuteAsync(sql, generation);
        }
        _time.Advance(TimeSpan.FromDays(2));
        var files = Files();
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await AppealCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedAppealDecisionAuditRollsBackAllProjectionsAndNeverConfirmsAResult(bool approve)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var original = await OriginalAppealFactAsync(data.Data.DecisionId);
        var service = new PhotoReviewAppealDecisions(Factory(new RefuseAppealDecisionAudit()), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.DecideAsync(data.Reviewer, data.Appeal.AppealId, 2, approve, Key(), Ct));
        Assert.Equal(original, await OriginalAppealFactAsync(data.Data.DecisionId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where id=$1 and status='Open' and version=2 and closed_at is null", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Data.Item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_appeal_decision_commands where appeal_id=$1", data.Appeal.AppealId));
    }

    private async Task<string> OriginalAppealFactAsync(Guid id)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(to_jsonb(d),(select jsonb_agg(to_jsonb(v) order by v.variant)
              from safety.photo_review_decision_variants v where v.decision_id=d.id))::text as "Value"
            from safety.photo_review_decisions d where d.id={id}
            """).SingleAsync(Ct);
    }

    [Fact]
    public async Task CatalogRejectsAnAppealWhoseOriginalManifestDiffersFromItsConsistentFinalManifest()
    {
        var data = await AssignedAppealForEvidenceAsync();
        Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, true, Key(), Ct));
        Assert.Single((await Catalog().CaptureAsync(10, 100_000_000, Ct)).Assets);
        // Deliberate corruption in an owned scratch DB, using the migrator. Runtime cannot rewrite this history.
        await using var connection = new NpgsqlConnection(_database!.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var corrupt = new NpgsqlCommand("update safety.photo_review_decision_variants set sha256=$2 where decision_id=$1 and variant='full'", connection);
        corrupt.Parameters.AddWithValue(data.Data.DecisionId);
        corrupt.Parameters.AddWithValue(new byte[32]);
        Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Catalog().CaptureAsync(10, 100_000_000, Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different")]
    public async Task ExpiredAppealWithDamagedCurrentManifestStillClosesWithoutAHumanDecision(string damage)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var generation = await CleanupGenerationAsync(data.Data.Item);
        var sql = damage == "missing"
            ? "delete from media.review_candidate_variants where generation=$1 and kind='full'"
            : "update media.review_candidate_variants set sha256=$2 where generation=$1 and kind='full'";
        if (damage == "missing")
        {
            await ExecuteAsync(sql, generation);
        }
        else
        {
            await ExecuteAsync(sql, generation, new byte[32]);
        }
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil);
        var service = new PhotoReviewAppealAvailability(Factory(), new FileSystemObjectStore(_root.FullName), _time);
        Assert.Equal(1, await service.ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where id=$1 and status='ClosedUnavailable'", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await AppealCleanup(source).DispatchOnceAsync(Ct));
        Assert.DoesNotContain(Files(), f => f.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppealThatExpiresDuringHealthyStorageReadClosesAtTheFreshClock()
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil.AddTicks(-10));
        var advanced = false;
        var store = new AvailabilityStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
        {
            if (!advanced)
            {
                advanced = true;
                _time.Advance(TimeSpan.FromTicks(10));
            }
            return Task.CompletedTask;
        });
        Assert.Equal(1, await new PhotoReviewAppealAvailability(Factory(), store, _time).ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.True(advanced);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where id=$1 and status='ClosedUnavailable' and closed_at=maximum_retention_until", data.Appeal.AppealId));
    }

    [Fact]
    public async Task AbandonedAppealCleanupLeaseCanBeReclaimedAndTheOldTokenCannotAcknowledgeOrRetry()
    {
        var data = await AssignedAppealForEvidenceAsync();
        Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(data.Reviewer,
            data.Appeal.AppealId, 2, false, Key(), Ct));
        _time.Advance(TimeSpan.FromDays(1));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var dispatcher = AppealCleanup(source);
        var abandoned = Assert.IsType<AppealCandidateCleanupClaim>(await dispatcher.ClaimAsync(Ct));
        Assert.Null(await dispatcher.ClaimAsync(Ct));
        _time.Advance(AppealCandidateCleanupDispatcher.LeaseDuration + TimeSpan.FromTicks(10));
        var reclaimed = Assert.IsType<AppealCandidateCleanupClaim>(await dispatcher.ClaimAsync(Ct));
        Assert.NotEqual(abandoned.Token, reclaimed.Token);
        Assert.False(await dispatcher.CompleteSweepAsync(abandoned, Ct));
        Assert.False(await dispatcher.RetryAsync(abandoned, Ct));
        _time.Advance(AppealCandidateCleanupDispatcher.LeaseDuration + TimeSpan.FromTicks(10));
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(Ct));
        Assert.False(await dispatcher.CompleteSweepAsync(reclaimed, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null and cleanup_token is null", reclaimed.Generation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialAppealDeleteAndConsumerCancellationRetainARecoverableObligation(bool cancel)
    {
        var data = await AssignedAppealForEvidenceAsync();
        Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(data.Reviewer,
            data.Appeal.AppealId, 2, false, Key(), Ct));
        var generation = await CleanupGenerationAsync(data.Data.Item);
        var original = await OriginalAppealFactAsync(data.Data.DecisionId);
        _time.Advance(TimeSpan.FromDays(1));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var deletes = 0;
        var store = new ApprovedCleanupStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
        {
            if (++deletes == 2)
            {
                if (cancel)
                {
                    shutdown.Cancel();
                }
                else
                {
                    throw new IOException("Synthetic partial appeal cleanup.");
                }
            }
            return Task.CompletedTask;
        });
        await using var source = NpgsqlDataSource.Create(_connectionString);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppealCleanup(source, store).DispatchOnceAsync(shutdown.Token));
        }
        else
        {
            Assert.Equal(1, await AppealCleanup(source, store).DispatchOnceAsync(Ct));
        }
        Assert.Equal(2, Files().Count(f => f.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is null and cleanup_requested_at is not null", generation));
        Assert.Equal(0, await AppealCleanup(source).DispatchOnceAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await AppealCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(original, await OriginalAppealFactAsync(data.Data.DecisionId));
        Assert.DoesNotContain(Files(), f => f.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainSystemAppealCommitPreservesAnIdempotentDurableObligation(bool afterCommit)
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil);
        var fault = new AppealSystemCommitFault(_connectionString, data.Appeal.AppealId, afterCommit);
        var service = new PhotoReviewAppealAvailability(Factory(fault), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.True(fault.Fired);
        Assert.Equal(afterCommit ? 1 : 0, await ScalarAsync("select count(*) from safety.photo_review_appeals where id=$1 and status='ClosedUnavailable'", data.Appeal.AppealId));
        Assert.Equal(afterCommit ? 0 : 1, await new PhotoReviewAppealAvailability(Factory(), new FileSystemObjectStore(_root.FullName), _time)
            .ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_appeal_unavailable'", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await AppealCleanup(source).DispatchOnceAsync(Ct));
    }

    [Fact]
    public async Task RefusedSystemAuditCannotCloseAppealOrCreateACleanupMarker()
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil);
        var service = new PhotoReviewAppealAvailability(Factory(new RefuseAppealSystemAudit()), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.ReconcileAsync(data.Appeal.AppealId, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeals where id=$1 and status='Open' and version=2", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.review_candidates where case_id=$1 and cleanup_requested_at is not null", data.Data.Item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_appeal_unavailable'", data.Appeal.AppealId));
    }

    private AppealCandidateCleanupDispatcher AppealCleanup(NpgsqlDataSource source, IObjectStore? store = null) => new(source,
        store ?? new FileSystemObjectStore(_root.FullName), _time, true, NullLogger<AppealCandidateCleanupDispatcher>.Instance);

    private sealed class RefuseAppealDecisionAudit : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is "safety.photo_review_appeal_approved" or "safety.photo_review_appeal_rejected"))
            {
                throw new IOException("Synthetic appeal audit refusal.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RefuseAppealSystemAudit : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is "safety.photo_review_appeal_unavailable"))
            {
                throw new IOException("Synthetic system appeal audit refusal.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AppealSystemCommitFault(string connectionString, Guid appealId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await ClosedAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before system appeal commit.");
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
            if (await ClosedAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost system appeal commit response.");
            }
        }

        private async Task<bool> ClosedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from safety.photo_review_appeals where id=$1 and status='ClosedUnavailable')", connection, transaction);
            command.Parameters.AddWithValue(appealId);
            return await command.ExecuteScalarAsync(ct) is true;
        }
    }
}
