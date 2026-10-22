using System.Data;
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

public sealed class PhotoReviewEvidenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-review-evidence-");
    private string _connectionString = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);

    public ValueTask DisposeAsync()
    {
        _root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EvidenceIsAuditedBeforeDeliveryAndReadsOnlyTheSanitizedVariantThroughIndependentContexts()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new CountingStore(new FileSystemObjectStore(_root.FullName));
        var factory = Factory();
        var opened = Assert.IsType<PhotoReviewEvidenceResult.Opened>(
            await new PhotoReviewEvidence(factory, store, _time).OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, Ct));
        await using var lease = opened.Lease;
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_evidence_viewed'", item.CaseId));
        var beforeReads = factory.Contexts.Count;
        var bytes = new byte[8];
        using var delivered = new MemoryStream();
        var seenContexts = beforeReads;
        int count;
        while ((count = await lease.ReadAsync(bytes, Ct)) != 0)
        {
            Assert.True(factory.Contexts.Count > seenContexts);
            Assert.All(factory.Contexts.Skip(seenContexts), context => Assert.Throws<ObjectDisposedException>(() => context.Set<Upload>().Any()));
            seenContexts = factory.Contexts.Count;
            delivered.Write(bytes, 0, count);
        }
        Assert.Equal(TestImages.WebP(64, 32), delivered.ToArray());
        Assert.True(factory.Contexts.Count > beforeReads);
        Assert.Equal(factory.Contexts.Count, factory.Contexts.Select(context => context.ContextId.InstanceId).Distinct().Count());
        Assert.All(factory.Contexts, context => Assert.Throws<ObjectDisposedException>(() => context.Set<Upload>().Any()));
        Assert.Single(store.Opened);
        Assert.Equal(StorageArea.ReviewCandidates, store.Opened[0].Area);
        Assert.DoesNotContain(store.Opened, key => key.Area is StorageArea.Quarantine or StorageArea.Derivatives);
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task AssignmentPermissionAndAnotherReviewersCaseNeverOpenStorage()
    {
        var (reviewer, item) = await PreparedAsync();
        var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
        var foreign = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
        var store = new CountingStore(new FileSystemObjectStore(_root.FullName));
        var service = new PhotoReviewEvidence(Factory(), store, _time);
        Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(await service.OpenAsync(assigner, item.CaseId, PhotoVariant.Full, Ct));
        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await service.OpenAsync(foreign, item.CaseId, PhotoVariant.Full, Ct));
        Assert.IsType<PhotoReviewEvidenceResult.NotFound>(await service.OpenAsync(reviewer, Guid.NewGuid(), PhotoVariant.Full, Ct));
        Assert.Empty(store.Opened);
    }

    [Fact]
    public async Task UnknownVariantNeverOpensAnyStorageObject()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new CountingStore(new FileSystemObjectStore(_root.FullName));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PhotoReviewEvidence(Factory(), store, _time)
            .OpenAsync(reviewer, item.CaseId, (PhotoVariant)int.MaxValue, Ct));
        Assert.Empty(store.Opened);
    }

    [Fact]
    public async Task AuditFailureNeverReturnsAnEvidenceLeaseOrApprovesThePhoto()
    {
        var (reviewer, item) = await PreparedAsync();
        var service = new PhotoReviewEvidence(Factory(new RefuseEvidenceAudit()), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_evidence_viewed'", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("session")]
    [InlineData("owner")]
    [InlineData("deadline")]
    public async Task RevocationBetweenChunksPreventsAnyMoreBytes(string change)
    {
        var (reviewer, item) = await PreparedAsync();
        if (change == "deadline")
        {
            _time.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));
            reviewer = await ModeratorAsync(PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide);
            var assigner = await ModeratorAsync(PhotoReviewCapability.Assign);
            await using var assignmentDb = Context();
            Assert.IsType<PhotoReviewAssignmentResult.Assigned>(await new PhotoReviewAssignments(assignmentDb, _time)
                .AssignAsync(assigner, item.CaseId, reviewer.UserId, 2, Key(), Ct));
        }
        var result = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await new PhotoReviewEvidence(
            Factory(), new FileSystemObjectStore(_root.FullName), _time).OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, Ct));
        await using var lease = result.Lease;
        var first = new byte[8];
        Assert.Equal(first.Length, await lease.ReadAsync(first, Ct));
        if (change == "grant")
        {
            await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: false);
        }
        else if (change == "session")
        {
            await ExecuteAsync("update identity.login_sessions set revoked_at=now() where id=$1", reviewer.SessionId);
        }
        else if (change == "owner")
        {
            await ExecuteAsync("update identity.users set status='Restricted' where id=$1", item.OwnerId);
        }
        else
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await using var proof = Context();
            await using var transaction = await proof.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
            Assert.True(await new PhotoReviewAccess(proof, _time).AuthorizeAsync(reviewer, PhotoReviewCapability.Evidence, Ct));
        }
        var next = Enumerable.Repeat((byte)0xa5, 8).ToArray();
        await Assert.ThrowsAsync<PhotoReviewEvidenceRevokedException>(() => lease.ReadAsync(next, Ct));
        Assert.All(next, value => Assert.Equal((byte)0xa5, value));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("owner")]
    public async Task StorageWaitKeepsSqlUnlockedAndReauthorizationDeniesTheRevokedRead(string change)
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new BlockingStore(new FileSystemObjectStore(_root.FullName));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var opening = new PhotoReviewEvidence(Factory(), store, _time).OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, budget.Token);
        try
        {
            Assert.Same(store.Entered.Task, await Task.WhenAny(store.Entered.Task, opening).WaitAsync(TimeSpan.FromSeconds(5), Ct));
            if (change == "grant")
            {
                await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, enabled: false);
            }
            else
            {
                await ExecuteAsync("update identity.users set status='Restricted' where id=$1", item.OwnerId);
            }
            Assert.False(opening.IsCompleted);
            store.Release();
            var result = await opening;
            if (change == "grant")
            {
                Assert.IsType<PhotoReviewEvidenceResult.Forbidden>(result);
            }
            else
            {
                Assert.IsType<PhotoReviewEvidenceResult.NotFound>(result);
            }
            Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_evidence_viewed'", item.CaseId));
        }
        finally
        {
            store.Release();
            await budget.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            PhotoReviewEvidenceResult? completed = null;
            try
            {
                completed = await opening.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (opening.IsCompleted && budget.IsCancellationRequested)
            {
            }
            if (completed is PhotoReviewEvidenceResult.Opened opened)
            {
                await opened.Lease.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            }
        }
    }

    [Fact]
    public async Task CallerCancellationObservesTheStorageReadAndNeverAuditsOrReturnsBytes()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new BlockingStore(new FileSystemObjectStore(_root.FullName));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var opening = new PhotoReviewEvidence(Factory(), store, _time).OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, canceled.Token);
        try
        {
            Assert.Same(store.Entered.Task, await Task.WhenAny(store.Entered.Task, opening).WaitAsync(TimeSpan.FromSeconds(5), Ct));
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.True(opening.IsCompleted);
            Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_evidence_viewed'", item.CaseId));
        }
        finally
        {
            await canceled.CancelAsync();
            store.Release();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            PhotoReviewEvidenceResult? completed = null;
            try
            {
                completed = await opening.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (opening.IsCompleted && canceled.IsCancellationRequested)
            {
            }
            if (completed is PhotoReviewEvidenceResult.Opened opened)
            {
                await opened.Lease.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            }
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("hash")]
    public async Task MissingOrCorruptPrivateBytesNeverBecomeAuditedDeliverableEvidence(string failure)
    {
        var (reviewer, item) = await PreparedAsync();
        var bytes = TestImages.WebP(64, 32);
        byte[]? payload = failure switch
        {
            "missing" => null,
            "short" => bytes[..^1],
            "long" => [.. bytes, (byte)0],
            _ => bytes.Select((value, index) => index == bytes.Length - 1 ? (byte)(value ^ 1) : value).ToArray(),
        };
        if (failure == "hash")
        {
            Assert.NotNull(payload);
            Assert.Equal(bytes.Length, payload.Length);
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(payload));
            Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(payload)));
        }
        var result = await new PhotoReviewEvidence(Factory(), new SubstituteReadStore(payload), _time)
            .OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, Ct);

        Assert.IsType<PhotoReviewEvidenceResult.Unavailable>(result);
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_evidence_viewed'", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task ConcurrentLeasesFromOneServiceUseIndependentDisposedContexts()
    {
        var (reviewer, item) = await PreparedAsync();
        var factory = Factory();
        var service = new PhotoReviewEvidence(factory, new FileSystemObjectStore(_root.FullName), _time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var firstOpening = service.OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, budget.Token);
        var secondOpening = service.OpenAsync(reviewer, item.CaseId, PhotoVariant.Card, budget.Token);
        try
        {
            var results = await Task.WhenAll(firstOpening, secondOpening);
            var first = Assert.IsType<PhotoReviewEvidenceResult.Opened>(results[0]).Lease;
            var second = Assert.IsType<PhotoReviewEvidenceResult.Opened>(results[1]).Lease;
            var firstBytes = new byte[8];
            var secondBytes = new byte[8];

            Assert.All(await Task.WhenAll(first.ReadAsync(firstBytes, budget.Token), second.ReadAsync(secondBytes, budget.Token)), count => Assert.Equal(8, count));

            Assert.Equal(TestImages.WebP(64, 32).Take(8), firstBytes);
            Assert.Equal(firstBytes, secondBytes);
            Assert.Equal(factory.Contexts.Count, factory.Contexts.Select(context => context.ContextId.InstanceId).Distinct().Count());
            Assert.All(factory.Contexts, context => Assert.Throws<ObjectDisposedException>(() => context.Set<Upload>().Any()));
        }
        finally
        {
            await budget.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(firstOpening, secondOpening).WaitAsync(cleanup.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && firstOpening.IsCompleted && secondOpening.IsCompleted)
            {
                // Task.WhenAll in the body already propagates this opening failure; observe both before cleanup.
            }
            var leases = new List<PhotoReviewEvidenceLease>();
            foreach (var task in new[] { firstOpening, secondOpening })
            {
                if (task.IsCompletedSuccessfully && await task is PhotoReviewEvidenceResult.Opened opened)
                {
                    leases.Add(opened.Lease);
                }
            }
            await Task.WhenAll(leases.Distinct().Select(lease => lease.DisposeAsync().AsTask())).WaitAsync(cleanup.Token);
        }
    }

    [Fact]
    public async Task AnAmbiguousLaterChunkCommitNeverReleasesThatChunksBytes()
    {
        var (reviewer, item) = await PreparedAsync();
        var commit = new LoseChunkCommitResponse();
        var result = Assert.IsType<PhotoReviewEvidenceResult.Opened>(await new PhotoReviewEvidence(
            Factory(commit), new FileSystemObjectStore(_root.FullName), _time).OpenAsync(reviewer, item.CaseId, PhotoVariant.Full, Ct));
        await using var lease = result.Lease;
        Assert.Equal(8, await lease.ReadAsync(new byte[8], Ct));
        commit.Armed = true;
        var destination = Enumerable.Repeat((byte)0xa5, 8).ToArray();

        await Assert.ThrowsAsync<IOException>(() => lease.ReadAsync(destination, Ct));

        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        Assert.True(commit.Fired);
    }

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

    private async Task<long> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 2 };
        command.Parameters.AddWithValue(id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CaseData(Guid CaseId, Guid OwnerId, Guid UploadId, long UploadVersion,
        DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline);

    private sealed class ContextFactory(DbContextOptions<DuoraDbContext> options) : IDbContextFactory<DuoraDbContext>
    {
        internal List<DuoraDbContext> Contexts { get; } = [];
        public DuoraDbContext CreateDbContext()
        {
            var context = new DuoraDbContext(options);
            lock (Contexts)
            {
                Contexts.Add(context);
            }
            return context;
        }
    }

    private sealed class CountingStore(IObjectStore inner) : IObjectStore
    {
        internal List<ObjectKey> Opened { get; } = [];
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Opened.Add(key);
            return inner.OpenReadAsync(key, cancellationToken);
        }
    }

    private sealed class BlockingStore(IObjectStore inner) : IObjectStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _released.TrySetResult();
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return await inner.OpenReadAsync(key, cancellationToken);
        }
    }

    private sealed class SubstituteReadStore(byte[]? bytes) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(bytes is null ? null : new MemoryStream(bytes, writable: false));
    }

    private sealed class LoseChunkCommitResponse : DbTransactionInterceptor
    {
        internal bool Armed { get; set; }
        internal bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Fired = true;
                throw new IOException("Synthetic lost response after chunk authorization commit");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RefuseEvidenceAudit : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal))
            {
                throw new IOException("Synthetic evidence audit refusal");
            }
            return ValueTask.FromResult(result);
        }
    }
}
