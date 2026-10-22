using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Media;

internal abstract record ReviewCandidateStageResult
{
    internal sealed record Staged(Guid CaseId) : ReviewCandidateStageResult;

    internal sealed record LeaseLost : ReviewCandidateStageResult;
}

/// <summary>
/// Prepares a private generation before any PUT, then coordinates Media and Safety in a short commit.
/// Not connected to admission or the processing pipeline until the complete human review flow exists.
/// </summary>
internal sealed class PhotoReviewCandidates(
    DuoraDbContext db, IObjectStore store, TimeProvider time, MediaProcessingOptions options)
{
    private sealed record Variant(PhotoVariant Kind, ObjectKey Key, byte[] Content, int Width, int Height, byte[] Sha256);

    private sealed record StoredVariant(string ObjectKey, long SizeBytes, byte[] Sha256);

    internal async Task<ReviewCandidateStageResult> StageAsync(
        Guid uploadId,
        long expectedVersion,
        DateTimeOffset leaseExpiresAt,
        SanitizeOutcome.Sanitized sanitized,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Storage preparation cannot run inside a caller's transaction.");
        }

        if (await ConfirmedCaseAsync(uploadId, expectedVersion, cancellationToken) is { } existing)
        {
            return new ReviewCandidateStageResult.Staged(existing);
        }

        var now = time.GetUtcNow();
        if (expectedVersion is < 1 or long.MaxValue || leaseExpiresAt <= now || leaseExpiresAt > now + options.LeaseDuration)
        {
            return new ReviewCandidateStageResult.LeaseLost();
        }

        var upload = await db.Set<Upload>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == uploadId, cancellationToken);
        if (upload is null || upload.State != UploadState.Processing || upload.Version != expectedVersion
            || upload.Fingerprint() is not { } fingerprint || fingerprint.Format != sanitized.Detected)
        {
            return new ReviewCandidateStageResult.LeaseLost();
        }

        var generation = Guid.NewGuid();
        var variants = ValidateAndFreeze(generation, sanitized);
        using var deadline = new CancellationTokenSource(leaseExpiresAt - now, time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var prepared = false;
        var writerSettled = true; // No request issued yet; changed before each PUT can reach storage.
        var committed = false;
        try
        {
            prepared = await PrepareAsync(upload, generation, leaseExpiresAt, sanitized, variants, budget.Token);
            if (!prepared)
            {
                return await ConfirmedCaseAsync(uploadId, expectedVersion, cancellationToken) is { } winner
                    ? new ReviewCandidateStageResult.Staged(winner)
                    : new ReviewCandidateStageResult.LeaseLost();
            }

            foreach (var variant in variants)
            {
                budget.Token.ThrowIfCancellationRequested();
                writerSettled = false;
                await using var content = new MemoryStream(variant.Content, writable: false);
                await store.PutAsync(variant.Key, content, variant.Content.LongLength, IPhotoSanitizer.VariantContentType, budget.Token);
                writerSettled = true;
            }

            // No further PUT is issued here, including after a late successful response outlives the budget.
            await SettleWriterAsync(generation);
            budget.Token.ThrowIfCancellationRequested();
            var caseId = await ConfirmAsync(upload, generation, leaseExpiresAt, budget.Token);
            if (caseId is null)
            {
                return new ReviewCandidateStageResult.LeaseLost();
            }

            committed = true;
            await DeleteOriginalQuietlyAsync(upload);
            return new ReviewCandidateStageResult.Staged(caseId.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new ReviewCandidateStageResult.LeaseLost();
        }
        finally
        {
            if (prepared && !committed)
            {
                await RetainCleanupAsync(generation, writerSettled);
            }
        }
    }

    private static Variant[] ValidateAndFreeze(Guid generation, SanitizeOutcome.Sanitized sanitized)
    {
        if (!Enum.IsDefined(sanitized.Detected) || sanitized.Width < 1 || sanitized.Height < 1
            || (long)sanitized.Width * sanitized.Height > 20_000_000 || sanitized.Variants.Count != 3
            || sanitized.Variants.Any(variant => !Enum.IsDefined(variant.Kind))
            || sanitized.Variants.Select(variant => variant.Kind).Distinct().Count() != 3)
        {
            throw new InvalidDataException("Invalid sanitized candidate shape.");
        }

        var result = new List<Variant>();
        long total = 0;
        foreach (var kind in PhotoVariants.All)
        {
            var variant = sanitized.Variants.Single(item => item.Kind == kind);
            var expected = PhotoVariants.Fit(sanitized.Width, sanitized.Height, PhotoVariants.MaxSide(kind));
            if ((variant.Width, variant.Height) != expected || variant.Content is not { Length: > 0 and <= 16_777_216 })
            {
                throw new InvalidDataException("Invalid sanitized candidate variant.");
            }

            total += variant.Content.LongLength;
            if (total > 25_165_824)
            {
                throw new InvalidDataException("Sanitized candidate exceeds its output envelope.");
            }

            var bytes = variant.Content.ToArray();
            result.Add(new Variant(kind, ObjectKey.ForReviewCandidate(generation, kind), bytes,
                variant.Width, variant.Height, SHA256.HashData(bytes)));
        }

        return [.. result];
    }

    private async Task<Guid?> ConfirmedCaseAsync(Guid uploadId, long version, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(options.LeaseDuration, time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var existing = await FindConfirmedCaseAsync(uploadId, version, budget.Token);
        if (existing is null)
        {
            return null;
        }

        var variants = await db.Database.SqlQuery<StoredVariant>($"""
            select v.object_key as "ObjectKey",v.size_bytes as "SizeBytes",v.sha256 as "Sha256"
            from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id={uploadId} and c.upload_version={version} and c.case_id={existing.Value}
            """).ToListAsync(budget.Token);
        if (variants.Count != 3 || variants.Any(variant => variant.SizeBytes is < 1 or > 16_777_216)
            || variants.Sum(variant => variant.SizeBytes) > 25_165_824)
        {
            return null;
        }

        foreach (var variant in variants)
        {
            var key = ObjectKey.Parse(variant.ObjectKey);
            if (key.Area != StorageArea.ReviewCandidates)
            {
                return null;
            }

            await using var stream = await store.OpenReadAsync(key, budget.Token);
            if (stream is null)
            {
                return null;
            }

            try
            {
                await using var exact = new ExactLengthStream(stream, variant.SizeBytes);
                var hash = await SHA256.HashDataAsync(exact, budget.Token);
                if (!CryptographicOperations.FixedTimeEquals(hash, variant.Sha256))
                {
                    return null;
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
            {
                return null;
            }
        }

        // Storage I/O holds no SQL lock. Revalidate current state, account and deadlines afterwards too.
        return await FindConfirmedCaseAsync(uploadId, version, budget.Token) == existing ? existing : null;
    }

    private async Task<Guid?> FindConfirmedCaseAsync(Guid uploadId, long version, CancellationToken cancellationToken)
    {
        if (version is < 1 or long.MaxValue)
        {
            return null;
        }

        var cases = await db.Database.SqlQuery<Guid>($"""
            select r.id as "Value" from media.review_candidates c
            join safety.photo_review_cases r on r.id=c.case_id and r.upload_id=c.upload_id
              and r.candidate_generation=c.generation
            join media.uploads u on u.id=c.upload_id
            join identity.users owner on owner.id=u.owner_id
            where c.upload_id={uploadId} and c.upload_version={version} and c.committed_at is not null
              and c.cleanup_requested_at is null and c.deleted_at is null
              and u.state='AwaitingReview' and u.version={version + 1} and owner.email_confirmed
              and owner.status in ('PendingEligibility','Active')
              and r.review_deadline>{time.GetUtcNow()} and c.retention_until>{time.GetUtcNow()}
            """).ToListAsync(cancellationToken);
        return cases.Count == 1 ? cases[0] : null;
    }

    private async Task<bool> PrepareAsync(
        Upload upload, Guid generation, DateTimeOffset leaseExpiresAt, SanitizeOutcome.Sanitized sanitized,
        IReadOnlyList<Variant> variants, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("set local lock_timeout='2s'", cancellationToken);
        var now = time.GetUtcNow();
        if (now >= leaseExpiresAt)
        {
            return false;
        }

        var inserted = await db.Database.ExecuteSqlAsync($"""
            insert into media.review_candidates
              (generation,upload_id,upload_version,detected_type,width,height,created_at,prepare_deadline,cleanup_available_at)
            select {generation},id,version,{PhotoFormats.ContentType(sanitized.Detected)},
              {sanitized.Width},{sanitized.Height},{now},{leaseExpiresAt},{now}
            from media.uploads where id={upload.Id} and version={upload.Version} and state='Processing'
            on conflict(upload_id,upload_version) do nothing
            """, cancellationToken);
        if (inserted != 1)
        {
            return false;
        }

        foreach (var variant in variants)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into media.review_candidate_variants(generation,kind,object_key,width,height,size_bytes,sha256)
                values({generation},{PhotoVariants.Name(variant.Kind)},{variant.Key.Value},
                  {variant.Width},{variant.Height},{variant.Content.LongLength},{variant.Sha256})
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<Guid?> ConfirmAsync(Upload upload, Guid generation, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("set local lock_timeout='2s'", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id) values({upload.OwnerId}) on conflict do nothing
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.user_guards where user_id={upload.OwnerId} for update", cancellationToken);
        // Account writers hold users, not only the Safety guard. Hold both through revalidation and commit.
        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={upload.OwnerId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={upload.Id} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={generation} for update", cancellationToken);
        var now = time.GetUtcNow();
        if (now >= leaseExpiresAt)
        {
            return null;
        }

        var allowed = await db.Database.SqlQuery<bool>($"""
            select exists(select 1 from media.uploads u join identity.users owner on owner.id=u.owner_id
              join media.review_candidates c on c.upload_id=u.id
              where u.id={upload.Id} and u.owner_id={upload.OwnerId} and u.version={upload.Version} and u.state='Processing'
                and u.quarantine_key={upload.QuarantineKey} and u.content_sha256={upload.ContentSha256!}
                and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
                and c.generation={generation} and c.upload_version={upload.Version} and c.committed_at is null
                and c.writer_settled_at is not null and c.cleanup_requested_at is null and c.deleted_at is null
                and c.prepare_deadline>{now}) as "Value"
            """).SingleAsync(cancellationToken);
        if (!allowed)
        {
            return null;
        }

        var caseId = Guid.CreateVersion7(now);
        await PhotoReviewCases.RequestAsync(db, time, caseId, upload.Id, generation, upload.Version, now, cancellationToken);
        var moved = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state='AwaitingReview',version=version+1
            where id={upload.Id} and state='Processing' and version={upload.Version}
            """, cancellationToken);
        var confirmed = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set committed_at={now},case_id={caseId},retention_until={now.AddHours(96)}
            where generation={generation} and committed_at is null and cleanup_requested_at is null
              and writer_settled_at is not null and prepare_deadline>{now}
            """, cancellationToken);
        if (moved != 1 || confirmed != 1)
        {
            throw new InvalidOperationException("Candidate or upload lost ownership during the guarded commit.");
        }

        await OriginalCleanupQueue.EnqueueAsync(db, upload.Id, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return caseId;
    }

    private async Task SettleWriterAsync(Guid generation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set writer_settled_at=coalesce(writer_settled_at,{time.GetUtcNow()})
            where generation={generation} and committed_at is null
            """, budget.Token);
    }

    private async Task RetainCleanupAsync(Guid generation, bool writerSettled)
    {
        try
        {
            if (writerSettled)
            {
                await SettleWriterAsync(generation);
            }

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var now = time.GetUtcNow();
            // Never abandon a COMMIT that succeeded with a lost response, nor reset a sweep's existing schedule/lease.
            await db.Database.ExecuteSqlAsync($"""
                update media.review_candidates
                set cleanup_available_at=case when cleanup_requested_at is null then {now} else cleanup_available_at end,
                  cleanup_requested_at=coalesce(cleanup_requested_at,{now})
                where generation={generation} and committed_at is null and deleted_at is null
                """, budget.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Prewritten metadata remains recoverable after its deadline even if this update fails.
        }
    }

    private async Task DeleteOriginalQuietlyAsync(Upload upload)
    {
        using var budget = new CancellationTokenSource(OriginalCleanupDispatcher.DeleteBudget);
        var key = ObjectKey.Parse(upload.QuarantineKey);
        try
        {
            await store.DeleteAsync(key, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            await OriginalCleanupQueue.CompleteImmediateAsync(db, upload.Id, key, budget.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // M2a's independent consumer owns the already committed original cleanup obligation.
        }
    }
}
