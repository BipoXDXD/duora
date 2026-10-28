using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence.Migrations;
using Duora.Infrastructure.Safety;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task ARejectionWrittenByThePreviousReleaseGainsTheDecidedCleanupObligation()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
            new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        var generation = await CleanupGenerationAsync(item);
        // The previous release kept the candidate 168 h for an appeal and never requested its cleanup.
        await MigratorExecuteAsync("""
            update safety.photo_review_decisions set candidate_retention_until_snapshot=decided_at+interval '168 hours'
            where case_id=$1
            """, item.CaseId);
        await MigratorExecuteAsync("""
            update media.review_candidates c set retention_until=d.candidate_retention_until_snapshot,cleanup_requested_at=null
            from safety.photo_review_decisions d where d.case_id=c.case_id and c.generation=$1
            """, generation);
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ApprovedCleanup(source).DispatchOnceAsync(Ct));

        await MigratorExecuteAsync(PhotoReviewWithoutAppeals.RequestCleanupOfExistingRejectionsSql);

        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join safety.photo_review_decisions d on d.case_id=c.case_id
            where c.generation=$1 and c.cleanup_requested_at=d.decided_at
            """, generation));
        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
    }

    [Fact]
    public async Task TheBackfillLeavesPendingAndApprovedCandidatesUntouched()
    {
        var (_, pending) = await PreparedAsync();
        var (reviewer, approved) = await PreparedAsync();
        Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, approved, Key()));
        var before = await CandidateMarkersAsync();

        await MigratorExecuteAsync(PhotoReviewWithoutAppeals.RequestCleanupOfExistingRejectionsSql);

        Assert.Equal(before, await CandidateMarkersAsync());
        Assert.Equal(0, await ScalarAsync("""
            select count(*) from media.review_candidates where case_id=$1 and cleanup_requested_at is not null
            """, pending.CaseId));
    }

    [Fact]
    public async Task TheMigrationRefusesADatabaseThatStillHasAnAppeal()
    {
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
            new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        await MigratorExecuteAsync("""
            insert into safety.photo_review_appeals(id,case_id,original_decision_id,owner_id,candidate_generation,policy_version,
              status,version,original_rejected_at,requested_at,response_target_at,maximum_retention_until)
            select gen_random_uuid(),d.case_id,d.id,u.owner_id,d.candidate_generation,d.policy_version,'Open',1,d.decided_at,
              d.decided_at,d.decided_at+interval '168 hours',d.decided_at+interval '720 hours'
            from safety.photo_review_decisions d join safety.photo_review_cases r on r.id=d.case_id
            join media.uploads u on u.id=r.upload_id where d.case_id=$1
            """, item.CaseId);

        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => MigratorExecuteAsync(PhotoReviewWithoutAppeals.RefuseExistingAppealsSql));

        Assert.Contains("photo_review_appeals", refused.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMigrationGivesUpQuicklyInsteadOfQueueingTrafficBehindItsExclusiveLock()
    {
        await using var reader = new NpgsqlConnection(_database!.MigratorConnectionString);
        await reader.OpenAsync(Ct);
        await using var held = await reader.BeginTransactionAsync(Ct);
        await using (var share = new NpgsqlCommand("lock table safety.photo_review_decisions in access share mode", reader, held))
        {
            await share.ExecuteNonQueryAsync(Ct);
        }

        await using var migrator = new NpgsqlConnection(_database.MigratorConnectionString);
        await migrator.OpenAsync(Ct);
        await using var transaction = await migrator.BeginTransactionAsync(Ct);
        // Without lock_timeout the ALTER would wait for the reader; the 30 s command timeout turns that into a failure.
        await using var up = new NpgsqlCommand(PhotoReviewWithoutAppeals.UpSql, migrator, transaction) { CommandTimeout = 30 };
        var started = System.Diagnostics.Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => up.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, refused.SqlState);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"Waited {started.Elapsed} for the lock.");
    }

    private async Task<string> CandidateMarkersAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select coalesce(string_agg(generation::text||':'||coalesce(cleanup_requested_at::text,'-'),',' order by generation),'')
            from media.review_candidates
            """, connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task MigratorExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_database!.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(Ct);
    }
}
