using System.Data;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewEvidenceResult
{
    internal sealed record Opened(PhotoReviewEvidenceLease Lease) : PhotoReviewEvidenceResult;
    internal sealed record Forbidden : PhotoReviewEvidenceResult;
    internal sealed record NotFound : PhotoReviewEvidenceResult;
    internal sealed record Unavailable : PhotoReviewEvidenceResult;
}

internal sealed class PhotoReviewEvidenceRevokedException() : IOException("Review evidence access is no longer available.");

/// <summary>Private bounded bytes; every delivered chunk has its own live committed authorization.</summary>
internal sealed class PhotoReviewEvidenceLease : IAsyncDisposable
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _reader = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _accepted = [];
    private readonly Func<CancellationToken, Task<bool>> _authorize;
    private byte[]? _buffer;
    private int _offset;
    private bool _stopped;
    private Task? _disposal;

    internal PhotoReviewEvidenceLease(byte[] buffer, Func<CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(authorize);
        _buffer = buffer;
        _authorize = authorize;
    }

    internal Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_state)
        {
            if (_stopped)
            {
                return Task.FromException<int>(new ObjectDisposedException(nameof(PhotoReviewEvidenceLease)));
            }
            _accepted.Add(completion.Task);
        }
        // The returned task is registered before the core starts, including reads queued on the semaphore.
        // CompleteReadAsync catches every outcome; DisposeAsync observes all accepted returned tasks.
        _ = CompleteReadAsync(completion, destination, cancellationToken);
        return completion.Task;
    }

    private async Task CompleteReadAsync(TaskCompletionSource<int> completion, Memory<byte> destination, CancellationToken cancellationToken)
    {
        try
        {
            completion.TrySetResult(await ReadCoreAsync(destination, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_state)
            {
                _accepted.Remove(completion.Task);
            }
        }
    }

    private async Task<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var entered = false;
        try
        {
            await _reader.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            linked.Token.ThrowIfCancellationRequested();
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (destination.IsEmpty || _offset == _buffer!.Length)
                {
                    return 0;
                }
            }
            if (!await _authorize(linked.Token).ConfigureAwait(false))
            {
                throw new PhotoReviewEvidenceRevokedException();
            }
            linked.Token.ThrowIfCancellationRequested();
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                var count = Math.Min(Math.Min(destination.Length, 64 * 1024), _buffer!.Length - _offset);
                _buffer.AsMemory(_offset, count).CopyTo(destination);
                _offset += count;
                return count;
            }
        }
        catch
        {
            // Never await DisposeAsync here: it must wait for this very read's returned task.
            StopAndClear();
            throw;
        }
        finally
        {
            if (entered)
            {
                _reader.Release();
            }
        }
    }

    private void StopAndClear()
    {
        bool cancel;
        lock (_state)
        {
            cancel = !_stopped;
            _stopped = true;
            ClearBuffer();
        }
        if (cancel)
        {
            _lifetime.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task[] accepted;
        TaskCompletionSource completion;
        lock (_state)
        {
            if (_disposal is not null)
            {
                return new ValueTask(_disposal);
            }
            _stopped = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            accepted = [.. _accepted];
        }
        _ = CompleteDisposalAsync(completion, accepted);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion, Task[] accepted)
    {
        Exception? cancellationFailure = null;
        try
        {
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
            try
            {
                await Task.WhenAll(accepted).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Read outcomes remain on their own completed tasks. Cleanup observes, never abandons them.
            }
            lock (_state)
            {
                ClearBuffer();
            }
            _lifetime.Dispose();
            _reader.Dispose();
            if (cancellationFailure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(cancellationFailure);
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void ClearBuffer()
    {
        if (_buffer is not null)
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = null;
        }
    }
}

/// <summary>Internal private evidence service; all contexts end before storage or consumer pauses.</summary>
internal sealed class PhotoReviewEvidence
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewEvidence(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    internal async Task<PhotoReviewEvidenceResult> OpenAsync(
        PhotoReviewActor actor, Guid caseId, PhotoVariant variant, CancellationToken cancellationToken)
    {
        _ = PhotoVariants.Name(variant);
        RequireNoAmbientTransaction();
        var initial = await GuardedAsync(actor, caseId, variant, expected: null, audit: false, cancellationToken);
        if (initial.Snapshot is not { } snapshot)
        {
            return Denied(initial);
        }
        var buffer = await ReadPrivateBytesAsync(snapshot, variant, cancellationToken);
        if (buffer is null)
        {
            return new PhotoReviewEvidenceResult.Unavailable();
        }
        try
        {
            var final = await GuardedAsync(actor, caseId, variant, snapshot, audit: true, cancellationToken);
            if (final.Snapshot is null)
            {
                return Denied(final);
            }
            var lease = new PhotoReviewEvidenceLease(buffer, async ct =>
                (await GuardedAsync(actor, caseId, variant, snapshot, audit: false, ct)).Snapshot is not null);
            buffer = null; // Ownership transfers only after access record, audit and COMMIT are confirmed.
            return new PhotoReviewEvidenceResult.Opened(lease);
        }
        finally
        {
            if (buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private async Task<byte[]?> ReadPrivateBytesAsync(Snapshot snapshot, PhotoVariant variant, CancellationToken cancellationToken)
    {
        if (snapshot.SizeBytes is < 1 or > 16_777_216 || snapshot.Sha256.Length != 32)
        {
            return null;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[]? buffer = null;
        var excess = new byte[1];
        var confirmed = false;
        try
        {
            await using (var stream = await _store.OpenReadAsync(ObjectKey.ForReviewCandidate(snapshot.Generation, variant), budget.Token))
            {
                if (stream is null)
                {
                    return null;
                }
                buffer = new byte[checked((int)snapshot.SizeBytes)];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset), budget.Token);
                    if (read == 0)
                    {
                        return null;
                    }
                    offset += read;
                }
                if (await stream.ReadAsync(excess, budget.Token) != 0
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer), snapshot.Sha256))
                {
                    return null;
                }
            }
            // Disposal is part of the observed storage operation; a disposal failure must also clear bytes.
            budget.Token.ThrowIfCancellationRequested();
            confirmed = true;
            return buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(excess);
            if (!confirmed && buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static PhotoReviewEvidenceResult Denied(Guarded result) => result.Forbidden
        ? new PhotoReviewEvidenceResult.Forbidden() : new PhotoReviewEvidenceResult.NotFound();

    private static void RequireNoAmbientTransaction()
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Evidence owns its complete READ COMMITTED phases.");
        }
    }

    private async Task<Guarded> GuardedAsync(PhotoReviewActor actor, Guid caseId, PhotoVariant variant,
        Snapshot? expected, bool audit, CancellationToken cancellationToken)
    {
        RequireNoAmbientTransaction();
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var access = new PhotoReviewAccess(db, _time);
        await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
            {
                return new Guarded(null, Forbidden: true);
            }
        }
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.candidate_generation as "Generation"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where r.id={caseId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return new Guarded(null);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId], cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
        {
            return new Guarded(null, Forbidden: true);
        }
        if (!present)
        {
            return new Guarded(null);
        }
        var kind = PhotoVariants.Name(variant);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovered.Generation} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidate_variants where generation={discovered.Generation} and kind={kind} for share
            """, cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
        {
            return new Guarded(null, Forbidden: true);
        }
        var now = _time.GetUtcNow();
        var canonical = ObjectKey.ForReviewCandidate(discovered.Generation, variant);
        var snapshot = await db.Database.SqlQuery<Snapshot>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",
              r.candidate_generation as "Generation",r.version as "CaseVersion",r.policy_version as "PolicyVersion",
              r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",
              v.size_bytes as "SizeBytes",v.sha256 as "Sha256"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join media.review_candidate_variants v on v.generation=c.generation and v.kind={kind}
            where r.id={caseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and r.candidate_generation={discovered.Generation} and v.object_key={canonical.Value}
              and u.state='AwaitingReview' and u.version=c.upload_version+1
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
              and r.review_deadline>{now} and c.retention_until>{now}
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.deleted_at is null
              and (select a.reviewer_id from safety.photo_review_assignments a where a.case_id=r.id
                order by a.case_version desc limit 1)={actor.UserId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null || (expected is not null && !Matches(expected, snapshot)))
        {
            return new Guarded(null);
        }
        if (audit)
        {
            var id = Guid.CreateVersion7(now);
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_evidence_accesses
                  (id,case_id,case_version,actor_id,candidate_generation,policy_version,variant,variant_sha256,occurred_at)
                values({id},{caseId},{snapshot.CaseVersion},{actor.UserId},{snapshot.Generation},{snapshot.PolicyVersion},
                  {kind},{snapshot.Sha256},{now})
                """, cancellationToken);
            await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
                AuditAction.PhotoReviewEvidenceViewed, AuditTargetTypes.PhotoReviewEvidenceAccess, id,
                targetVersion: snapshot.CaseVersion), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new Guarded(snapshot);
    }

    private static async Task<bool> GuardUsersAsync(DuoraDbContext db, Guid[] users, CancellationToken cancellationToken)
    {
        var ordered = users.Distinct().Order().ToArray();
        var present = true;
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.user_guards(user_id) select id from identity.users where id={user} on conflict do nothing
                """, cancellationToken);
            var rows = await db.Database.SqlQuery<Guid>($"""
                select user_id as "Value" from safety.user_guards where user_id={user} for update
                """).ToListAsync(cancellationToken);
            present &= rows.Count == 1;
        }
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={user} for share", cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id={user} order by ur.role_id for share of ur,r nowait
                """, cancellationToken);
        }
        return present;
    }

    private static bool Matches(Snapshot first, Snapshot current) => first.OwnerId == current.OwnerId
        && first.UploadId == current.UploadId && first.UploadVersion == current.UploadVersion
        && first.Generation == current.Generation && first.CaseVersion == current.CaseVersion
        && first.PolicyVersion == current.PolicyVersion && first.ReviewDeadline == current.ReviewDeadline
        && first.RetentionUntil == current.RetentionUntil && first.SizeBytes == current.SizeBytes
        && CryptographicOperations.FixedTimeEquals(first.Sha256, current.Sha256);

    private sealed record Guarded(Snapshot? Snapshot, bool Forbidden = false);
    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid Generation);
    private sealed record Snapshot(Guid OwnerId, Guid UploadId, long UploadVersion, Guid Generation, long CaseVersion,
        string PolicyVersion, DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil, long SizeBytes, byte[] Sha256)
    {
        public override string ToString() => "PhotoReviewEvidenceSnapshot";
    }
}
