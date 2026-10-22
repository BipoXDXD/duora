using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
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

public sealed class PhotoReviewAssignmentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-review-assignment-");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] SummaryFields = ["awaitingReviewAt", "caseId", "reviewDeadline", "state", "version"];
    private string _connectionString = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);

    public ValueTask DisposeAsync()
    {
        _root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AssignmentCommitsVersionAuditAndReceiptWithoutGrantingImageAccessOrApprovingTheUpload()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        var unrelated = await CaseAsync();
        var sentinel = ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Full);
        var sentinelBytes = TestImages.WebP(64, 32);
        using (var content = new MemoryStream(sentinelBytes))
        {
            await new FileSystemObjectStore(_root.FullName).PutAsync(sentinel, content, sentinelBytes.Length, "image/webp", Ct);
        }
        var before = await StorageSnapshotAsync();

        var assigned = Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key()));

        Assert.Equal((item.CaseId, 2L), (assigned.CaseId, assigned.Version));
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from ops.audit_events where target_id=$1 and actor_kind='Moderator'
              and action='safety.photo_review_assigned' and target_type='photo_review_case' and target_version=2
            """, item.CaseId));
        Assert.Equal(item.UploadVersion, await ScalarAsync("select version from media.uploads where id=$1", item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
        Assert.IsType<PhotoReviewDetailsResult.Forbidden>(await FindAsync(actor, item.CaseId));
        Assert.IsType<PhotoReviewListResult.Forbidden>(await ListAsync(actor));
        var found = Assert.IsType<PhotoReviewDetailsResult.Found>(await FindAsync(reviewer, item.CaseId));
        Assert.Equal("human-review-v1", found.Details.PolicyVersion);
        Assert.Equal(item.ReviewDeadline, found.Details.Case.ReviewDeadline);
        Assert.Equal(before.OrderBy(value => value.Key), (await StorageSnapshotAsync()).OrderBy(value => value.Key));
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", unrelated.CaseId));
        Assert.Equal(unrelated.UploadVersion, await ScalarAsync("select version from media.uploads where id=$1", unrelated.UploadId));
    }

    [Fact]
    public async Task ReassignmentRemovesTheOldReviewersMetadataScopeAndDoesNotExtendTheDeadline()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var first = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var second = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, first.UserId, 1, Key()));
        Assert.IsType<PhotoReviewDetailsResult.Found>(await FindAsync(first, item.CaseId));

        var result = Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, second.UserId, 2, Key()));

        Assert.Equal(3, result.Version);
        Assert.IsType<PhotoReviewDetailsResult.NotFound>(await FindAsync(first, item.CaseId));
        Assert.Empty(Assert.IsType<PhotoReviewListResult.Listed>(await ListAsync(first)).Page.Cases);
        var found = Assert.IsType<PhotoReviewDetailsResult.Found>(await FindAsync(second, item.CaseId));
        Assert.Equal(item.ReviewDeadline, found.Details.Case.ReviewDeadline);
        Assert.Equal(2, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task ConfirmedReplayPrecedesNewVersionAndTargetPreconditionsWithoutDuplicatingTheEffect()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var first = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var second = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        var key = Key();
        var initial = Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, first.UserId, 1, key));
        Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, second.UserId, 2, Key()));
        await GrantAsync(first.UserId, PhotoReviewCapability.Evidence, enabled: false);

        Assert.Equal(initial, Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, first.UserId, 1, key)));
        Assert.IsType<PhotoReviewAssignmentResult.KeyReused>(await AssignAsync(actor, item.CaseId, second.UserId, 1, key));
        Assert.Equal(3, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(2, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(2, await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId));
        await GrantAsync(actor.UserId, PhotoReviewCapability.Assign, enabled: false);
        Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await AssignAsync(actor, item.CaseId, first.UserId, 1, key));
    }

    [Fact]
    public async Task TwoAssignmentsForTheSameExpectedVersionProduceOnlyOneHistoryEntry()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var first = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var second = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();

        var results = await Task.WhenAll(AssignAsync(actor, item.CaseId, first.UserId, 1, Key()),
            AssignAsync(actor, item.CaseId, second.UserId, 1, Key()));

        Assert.Single(results, value => value is PhotoReviewAssignmentResult.Assigned);
        Assert.Single(results, value => value is PhotoReviewAssignmentResult.PreconditionFailed);
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task AnEnabledReviewerCanBeAssignedWithoutAnActiveSessionButCannotReadUntilAuthenticated()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        await ExecuteAsync("update identity.login_sessions set revoked_at=now() where id=$1", reviewer.SessionId);

        Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key()));
        Assert.IsType<PhotoReviewDetailsResult.Forbidden>(await FindAsync(reviewer, item.CaseId));
        Assert.IsType<PhotoReviewAssignmentResult.NotFound>(await AssignAsync(actor, Guid.NewGuid(), reviewer.UserId, 1, Key()));
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("decide")]
    [InlineData("role")]
    [InlineData("mfa")]
    [InlineData("restricted")]
    [InlineData("missing")]
    public async Task AnUnavailableReviewerNeverChangesTheCase(string condition)
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        if (condition == "evidence" || condition == "decide")
        {
            await GrantAsync(reviewer.UserId, condition == "evidence" ? PhotoReviewCapability.Evidence : PhotoReviewCapability.Decide, enabled: false);
        }
        else if (condition == "role")
        {
            await ExecuteAsync("delete from identity.user_roles where user_id=$1", reviewer.UserId);
        }
        else if (condition == "mfa")
        {
            await ExecuteAsync("update identity.users set two_factor_enabled=false where id=$1", reviewer.UserId);
        }
        else if (condition == "restricted")
        {
            await ExecuteAsync("update identity.users set status='Restricted' where id=$1", reviewer.UserId);
        }

        var target = condition == "missing" ? Guid.NewGuid() : reviewer.UserId;
        Assert.IsType<PhotoReviewAssignmentResult.ReviewerUnavailable>(await AssignAsync(actor, item.CaseId, target, 1, Key()));
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Theory]
    [InlineData(AccountRoles.Operator)]
    [InlineData(AccountRoles.ProductAdmin)]
    public async Task AStoredGrantDoesNotSurviveChangingTheActorToAnotherPrivilegedRole(string role)
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        await ExecuteAsync("update identity.user_roles set role_id=(select id from identity.roles where name=$2) where user_id=$1", actor.UserId, role);

        Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key(), new RefuseCaseLookup()));
        Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await AssignAsync(actor, Guid.NewGuid(), reviewer.UserId, 1, Key(), new RefuseCaseLookup()));
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
    }

    [Fact]
    public async Task EvidenceAndAssignmentCapabilitiesDoNotImplyEachOther()
    {
        var evidenceOnly = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();

        Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await AssignAsync(evidenceOnly, item.CaseId, evidenceOnly.UserId, 1, Key(), new RefuseCaseLookup()));
        Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await AssignAsync(evidenceOnly, Guid.NewGuid(), evidenceOnly.UserId, 1, Key(), new RefuseCaseLookup()));
        Assert.IsType<PhotoReviewDetailsResult.NotFound>(await FindAsync(evidenceOnly, item.CaseId));
        Assert.Empty(Assert.IsType<PhotoReviewListResult.Listed>(await ListAsync(evidenceOnly)).Page.Cases);
    }

    [Fact]
    public async Task AssignedListsAreScopedPaginatedAndContainNoOwnerOrStorageIdentifiers()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var other = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var items = new List<CaseData>();
        for (var index = 0; index < 4; index++)
        {
            var item = await CaseAsync();
            Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key()));
            items.Add(item);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var foreign = await CaseAsync();
        Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await AssignAsync(actor, foreign.CaseId, other.UserId, 1, Key()));
        var page = Assert.IsType<PhotoReviewListResult.Listed>(await ListAsync(reviewer, limit: 2)).Page;
        Assert.Equal(items.Take(2).Select(value => value.CaseId), page.Cases.Select(value => value.CaseId));
        Assert.NotNull(page.NextCursor);
        var next = Assert.IsType<PhotoReviewListResult.Listed>(await ListAsync(reviewer, page.NextCursor, 2)).Page;
        Assert.Equal(items.Skip(2).Select(value => value.CaseId), next.Cases.Select(value => value.CaseId));
        Assert.Null(next.NextCursor);
        Assert.IsType<PhotoReviewDetailsResult.NotFound>(await FindAsync(reviewer, foreign.CaseId));
        foreach (var row in page.Cases.Concat(next.Cases))
        {
            var text = JsonSerializer.Serialize(row, Json);
            using var document = JsonDocument.Parse(text);
            Assert.Equal(SummaryFields,
                document.RootElement.EnumerateObject().Select(property => property.Name).Order());
            Assert.DoesNotContain("quarantine", text, StringComparison.Ordinal);
            Assert.DoesNotContain("review-candidates", text, StringComparison.Ordinal);
            Assert.DoesNotContain(items.Single(value => value.CaseId == row.CaseId).OwnerId.ToString("D"), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnAuditFailureRollsBackVersionHistoryAndReceipt()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();

        await Assert.ThrowsAsync<IOException>(() => AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key(), new RefuseAssignmentAudit()));

        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task AnApprovedUploadCannotBeAssignedByReusingItsOldReviewCase()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        await ExecuteAsync("update media.uploads set state='Approved',version=version+1 where id=$1", item.UploadId);

        Assert.IsType<PhotoReviewAssignmentResult.StateConflict>(await AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key()));
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("reviewer")]
    [InlineData("deadline")]
    public async Task AuthorizationAndDeadlineAreRecheckedAfterWaitingForTheLowerOwnersGuard(string change)
    {
        var item = await CaseAsync(Guid.Parse("00000000-0000-4000-8000-000000000001"));
        if (change == "deadline")
        {
            _time.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));
        }
        var actor = await ActorAsync(AccountRoles.Moderator, Guid.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff"));
        await GrantAsync(actor.UserId, PhotoReviewCapability.Assign, enabled: true);
        var reviewer = await ActorAsync(AccountRoles.Moderator, Guid.Parse("10000000-0000-4000-8000-000000000001"));
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: true);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, enabled: true);
        await using var owner = new NpgsqlConnection(_connectionString);
        await owner.OpenAsync(Ct);
        await using var held = await owner.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("select 1 from safety.user_guards where user_id=$1 for update", owner, held))
        {
            command.Parameters.AddWithValue(item.OwnerId);
            Assert.NotNull(await command.ExecuteScalarAsync(Ct));
        }
        await using var db = Context();
        await db.Database.OpenConnectionAsync(Ct);
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var assignment = new PhotoReviewAssignments(db, _time).AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key(), budget.Token);
        try
        {
            await WaitForLockAsync(pid, assignment, budget.Token);
            if (change == "deadline")
            {
                _time.Advance(TimeSpan.FromSeconds(2));
            }
            else
            {
                await GrantAsync(change == "actor" ? actor.UserId : reviewer.UserId,
                    change == "actor" ? PhotoReviewCapability.Assign : PhotoReviewCapability.Evidence, enabled: false);
            }
            Assert.False(assignment.IsCompleted);
            await held.CommitAsync(budget.Token);
            var result = await assignment;
            if (change == "actor")
            {
                Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(result);
            }
            else if (change == "reviewer")
            {
                Assert.IsType<PhotoReviewAssignmentResult.ReviewerUnavailable>(result);
            }
            else
            {
                Assert.IsType<PhotoReviewAssignmentResult.StateConflict>(result);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (held.Connection is not null)
            {
                await held.RollbackAsync(cleanup.Token);
            }
            await budget.CancelAsync();
            try
            {
                await assignment.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && assignment.IsCompleted)
            {
            }
        }
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task CrossedAssignmentsDiscoverTogetherAndAcquireAllUserGuardsWithoutDeadlock()
    {
        var firstCase = await CaseAsync(Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var secondCase = await CaseAsync(Guid.Parse("00000000-0000-4000-8000-000000000002"));
        var firstActor = await ModeratorAsync(PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var secondActor = await ModeratorAsync(PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var barrier = new DiscoveryBarrier();
        await using var firstDb = Context(barrier);
        await using var secondDb = Context(barrier);
        var first = new PhotoReviewAssignments(firstDb, _time).AssignAsync(firstActor, firstCase.CaseId, secondActor.UserId, 1, Key(), budget.Token);
        var second = new PhotoReviewAssignments(secondDb, _time).AssignAsync(secondActor, secondCase.CaseId, firstActor.UserId, 1, Key(), budget.Token);
        try
        {
            var results = await Task.WhenAll(first, second);
            Assert.All(results, value => Assert.IsType<PhotoReviewAssignmentResult.Assigned>(value));
            Assert.Equal(2, barrier.Arrivals);
            Assert.Equal(0, barrier.LockErrors);
        }
        finally
        {
            barrier.Release();
            await budget.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await Task.WhenAll(first, second).WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && first.IsCompleted && second.IsCompleted)
            {
            }
        }
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", firstCase.CaseId));
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", secondCase.CaseId));
    }

    [Fact]
    public async Task SessionExpiryWhileTheCaseRowIsLockedIsCheckedBeforeTheAssignmentEffect()
    {
        var actor = await ModeratorAsync(PhotoReviewCapability.Assign);
        var reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var item = await CaseAsync();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var held = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("select 1 from safety.photo_review_cases where id=$1 for update", connection, held))
        {
            command.Parameters.AddWithValue(item.CaseId);
            Assert.NotNull(await command.ExecuteScalarAsync(Ct));
        }
        await using var db = Context();
        await db.Database.OpenConnectionAsync(Ct);
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var assignment = new PhotoReviewAssignments(db, _time).AssignAsync(actor, item.CaseId, reviewer.UserId, 1, Key(), budget.Token);
        try
        {
            await WaitForLockAsync(pid, assignment, budget.Token);
            _time.Advance(TimeSpan.FromMinutes(10));
            Assert.False(assignment.IsCompleted);
            await held.CommitAsync(budget.Token);
            Assert.IsType<PhotoReviewAssignmentResult.Forbidden>(await assignment);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (held.Connection is not null)
            {
                await held.RollbackAsync(cleanup.Token);
            }
            await budget.CancelAsync();
            try
            {
                await assignment.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && assignment.IsCompleted)
            {
            }
        }
        Assert.Equal(1, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_assigned'", item.CaseId));
    }

    private async Task WaitForLockAsync(int pid, Task operation, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=$1 and wait_event_type='Lock')", connection);
            command.Parameters.AddWithValue(pid);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }
            Assert.False(operation.IsCompleted, "The assignment must actually wait on the owner's lower UUID guard.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        Assert.Fail("The database did not observe the assignment waiting on a user guard.");
    }

    private async Task<Dictionary<string, string>> StorageSnapshotAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_root.FullName, "*", SearchOption.AllDirectories))
        {
            result.Add(Path.GetRelativePath(_root.FullName, path), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))));
        }
        return result;
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

    private async Task<PhotoReviewAssignmentResult> AssignAsync(
        PhotoReviewActor actor, Guid caseId, Guid reviewerId, long version, IdempotencyKey key, params IInterceptor[] interceptors)
    {
        await using var db = Context(interceptors);
        return await new PhotoReviewAssignments(db, _time).AssignAsync(actor, caseId, reviewerId, version, key, Ct);
    }

    private async Task<PhotoReviewListResult> ListAsync(PhotoReviewActor actor, PhotoReviewCursor? cursor = null, int limit = 50)
    {
        await using var db = Context();
        return await new PhotoReviewAssignments(db, _time).ListAssignedAsync(actor, cursor, limit, Ct);
    }

    private async Task<PhotoReviewDetailsResult> FindAsync(PhotoReviewActor actor, Guid caseId)
    {
        await using var db = Context();
        return await new PhotoReviewAssignments(db, _time).FindAssignedAsync(actor, caseId, Ct);
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
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CaseData(Guid CaseId, Guid OwnerId, Guid UploadId, long UploadVersion,
        DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline);

    private sealed class RefuseCaseLookup : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("safety.photo_review_cases", command.CommandText, StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DiscoveryBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        private int _lockErrors;
        internal int Arrivals => Volatile.Read(ref _arrivals);
        internal int LockErrors => Volatile.Read(ref _lockErrors);
        internal void Release() => _both.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("safety.photo_review_cases", StringComparison.Ordinal)
                && command.CommandText.Contains("as \"OwnerId\"", StringComparison.Ordinal)
                && !command.CommandText.Contains("for update", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    Release();
                }
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException { SqlState: "40P01" or "55P03" })
            {
                Interlocked.Increment(ref _lockErrors);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RefuseAssignmentAudit : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal))
            {
                throw new IOException("Synthetic assignment audit refusal");
            }
            return ValueTask.FromResult(result);
        }
    }
}
