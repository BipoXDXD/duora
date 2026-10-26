using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record ReviewPromotionRequest(
    Guid CaseId, long CaseVersion, long UploadVersion, Guid CandidateGeneration, string PolicyVersion);

internal abstract record ReviewPromotionResult
{
    internal sealed record Prepared(Guid Generation) : ReviewPromotionResult;

    internal sealed record Unavailable : ReviewPromotionResult;
}

/// <summary>Private generation-owned copies, without approving or publishing a photo. Not registered.</summary>
internal sealed partial class PhotoReviewPromotions(DuoraDbContext db, IObjectStore store, TimeProvider time)
{
    private sealed record CandidateState(Guid UploadId, Guid OwnerId, long UploadVersion, long CaseVersion,
        Guid CandidateGeneration, string PolicyVersion, string DetectedType, int Width, int Height,
        DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil);

    private sealed record StoredVariant(string Kind, string ObjectKey, int Width, int Height, long SizeBytes, byte[] Sha256);

    private sealed record Variant(PhotoVariant Kind, ObjectKey Source, ObjectKey Destination,
        int Width, int Height, long SizeBytes, byte[] Sha256);

    private sealed record Snapshot(CandidateState State, Variant[] Variants);

    internal Task<ReviewPromotionResult> PrepareAsync(ReviewPromotionRequest request, CancellationToken cancellationToken) =>
        PrepareCoreAsync(request, null, cancellationToken);

    internal Task<ReviewPromotionResult> PrepareAppealAsync(ReviewAppealPromotionRequest request, CancellationToken cancellationToken) =>
        PrepareCoreAsync(request.Candidate, request, cancellationToken);

    private async Task<ReviewPromotionResult> PrepareCoreAsync(ReviewPromotionRequest request,
        ReviewAppealPromotionRequest? appeal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Promotion preparation cannot run in a caller's transaction.");
        }

        if (string.IsNullOrEmpty(request.PolicyVersion) || request.PolicyVersion.Length > 64)
        {
            return new ReviewPromotionResult.Unavailable();
        }

        var generation = Guid.NewGuid();
        var instant = time.GetUtcNow();
        // PostgreSQL persists microseconds. Use that same deadline rather than a later unpersisted fraction.
        var createdAt = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        var deadline = createdAt.AddSeconds(15);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var prepared = false;
        var writerStarted = false;
        var writerSettled = true;
        var confirmed = false;
        try
        {
            var snapshot = await PrepareMetadataAsync(request, appeal, generation, createdAt, deadline, budget.Token);
            if (snapshot is null)
            {
                return new ReviewPromotionResult.Unavailable();
            }

            prepared = true; // Only a confirmed metadata COMMIT permits storage I/O.
            foreach (var variant in snapshot.Variants)
            {
                EnsureLive(deadline, budget.Token);
                var bytes = await ReadBytesAsync(variant, budget.Token);
                if (bytes is null)
                {
                    return new ReviewPromotionResult.Unavailable();
                }

                try
                {
                    EnsureLive(deadline, budget.Token);
                    if (!writerStarted)
                    {
                        if (!await BeginWritingAsync(generation, deadline, budget.Token))
                        {
                            return new ReviewPromotionResult.Unavailable();
                        }

                        writerStarted = true;
                    }

                    // Recheck after the conditional UPDATE/COMMIT and any row-lock wait too.
                    EnsureLive(deadline, budget.Token);
                    writerSettled = false;
                    await using (var content = new MemoryStream(bytes, writable: false))
                    {
                        await store.PutAsync(variant.Destination, content, bytes.LongLength,
                            IPhotoSanitizer.VariantContentType, budget.Token);
                        writerSettled = true;
                    }

                    EnsureLive(deadline, budget.Token);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            await SettleWriterAsync(generation, budget.Token);
            EnsureLive(deadline, budget.Token);
            if (!await ConfirmPreparedAsync(request, appeal, generation, snapshot, deadline, budget.Token))
            {
                return new ReviewPromotionResult.Unavailable();
            }

            EnsureLive(deadline, budget.Token);
            var finishedAt = time.GetUtcNow();
            if (snapshot.State.ReviewDeadline <= finishedAt || snapshot.State.RetentionUntil <= finishedAt)
            {
                return new ReviewPromotionResult.Unavailable();
            }

            confirmed = true;
            return new ReviewPromotionResult.Prepared(generation);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReviewPromotionResult.Unavailable();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NpgsqlException or DbUpdateException)
        {
            return new ReviewPromotionResult.Unavailable();
        }
        finally
        {
            if (prepared && !confirmed)
            {
                await RetainCleanupAsync(generation, writerSettled);
            }
        }
    }

    private void EnsureLive(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (time.GetUtcNow() >= deadline)
        {
            throw new OperationCanceledException("Promotion preparation deadline elapsed.");
        }
    }

    private async Task<Snapshot?> PrepareMetadataAsync(ReviewPromotionRequest request, ReviewAppealPromotionRequest? appeal, Guid generation,
        DateTimeOffset createdAt, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("set local lock_timeout='2s'", cancellationToken);
        var snapshot = await LoadAsync(request, appeal, generation, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        EnsureLive(deadline, cancellationToken);
        var state = snapshot.State;
        await db.Database.ExecuteSqlAsync($"""
            insert into media.review_promotions
              (generation,upload_id,case_id,case_version,upload_version,candidate_generation,policy_version,
               detected_type,width,height,created_at,prepare_deadline,writer_settled_at,cleanup_available_at,appeal_id,appeal_version)
            values({generation},{state.UploadId},{request.CaseId},{state.CaseVersion},{state.UploadVersion},
              {state.CandidateGeneration},{state.PolicyVersion},{state.DetectedType},{state.Width},{state.Height},
              {createdAt},{deadline},{createdAt},{createdAt},{(appeal == null ? (Guid?)null : appeal.AppealId)},
              {(appeal == null ? (long?)null : appeal.AppealVersion)})
            """, cancellationToken);
        foreach (var variant in snapshot.Variants)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into media.review_promotion_variants(generation,kind,object_key,width,height,size_bytes,sha256)
                values({generation},{PhotoVariants.Name(variant.Kind)},{variant.Destination.Value},{variant.Width},
                  {variant.Height},{variant.SizeBytes},{variant.Sha256})
                """, cancellationToken);
        }

        EnsureLive(deadline, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private async Task<bool> BeginWritingAsync(Guid generation, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("set local lock_timeout='2s'", cancellationToken);
        var now = time.GetUtcNow();
        var changed = await db.Database.ExecuteSqlAsync($"""
            update media.review_promotions set writer_settled_at=null
            where generation={generation} and writer_settled_at is not null and prepare_deadline={deadline}
              and prepare_deadline>{now} and committed_at is null and asset_id is null
              and cleanup_requested_at is null and cleanup_token is null and deleted_at is null
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    private Task<int> SettleWriterAsync(Guid generation, CancellationToken cancellationToken) => db.Database.ExecuteSqlAsync($"""
        update media.review_promotions set writer_settled_at=coalesce(writer_settled_at,greatest(created_at,{time.GetUtcNow()}))
        where generation={generation} and committed_at is null and deleted_at is null
        """, cancellationToken);

    private async Task<bool> ConfirmPreparedAsync(ReviewPromotionRequest request, ReviewAppealPromotionRequest? appeal, Guid generation,
        Snapshot expected, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("set local lock_timeout='2s'", cancellationToken);
        var current = await LoadAsync(request, appeal, generation, cancellationToken);
        if (current is null || current.State != expected.State || current.Variants.Length != expected.Variants.Length)
        {
            return false;
        }

        for (var index = 0; index < current.Variants.Length; index++)
        {
            var actual = current.Variants[index];
            var original = expected.Variants[index];
            if (actual.Kind != original.Kind || actual.Source != original.Source || actual.Destination != original.Destination
                || actual.Width != original.Width || actual.Height != original.Height || actual.SizeBytes != original.SizeBytes
                || !CryptographicOperations.FixedTimeEquals(actual.Sha256, original.Sha256))
            {
                return false;
            }
        }

        await db.Database.ExecuteSqlAsync($"select 1 from media.review_promotions where generation={generation} for update", cancellationToken);
        EnsureLive(deadline, cancellationToken);
        var now = time.GetUtcNow();
        if (current.State.ReviewDeadline <= now || current.State.RetentionUntil <= now)
        {
            return false;
        }

        var state = current.State;
        var matching = await db.Database.SqlQuery<Guid>($"""
            select generation as "Value" from media.review_promotions
            where generation={generation} and upload_id={state.UploadId} and case_id={request.CaseId}
              and case_version={state.CaseVersion} and upload_version={state.UploadVersion}
              and candidate_generation={state.CandidateGeneration} and policy_version={state.PolicyVersion}
              and detected_type={state.DetectedType} and width={state.Width} and height={state.Height}
              and appeal_id is not distinct from {(appeal == null ? (Guid?)null : appeal.AppealId)}
              and appeal_version is not distinct from {(appeal == null ? (long?)null : appeal.AppealVersion)}
              and prepare_deadline={deadline} and prepare_deadline>{now} and writer_settled_at is not null
              and committed_at is null and asset_id is null and cleanup_requested_at is null
              and cleanup_token is null and deleted_at is null
            """).ToListAsync(cancellationToken);
        if (matching.Count != 1)
        {
            return false;
        }

        EnsureLive(deadline, cancellationToken);
        now = time.GetUtcNow();
        if (state.ReviewDeadline <= now || state.RetentionUntil <= now)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task RetainCleanupAsync(Guid generation, bool writerSettled)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var now = time.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                update media.review_promotions
                set cleanup_requested_at=coalesce(cleanup_requested_at,{now}),
                  cleanup_available_at=least(cleanup_available_at,{now}),
                  writer_settled_at=case when {writerSettled} then coalesce(writer_settled_at,greatest(created_at,{now}))
                    else writer_settled_at end
                where generation={generation} and committed_at is null and deleted_at is null
                """, cleanup.Token);
        }
        catch (Exception exception) when (exception is IOException or NpgsqlException or OperationCanceledException or InvalidOperationException)
        {
            // The confirmed initial intent/deadline still belongs to the durable sweeper, including after restart.
        }
    }

    // Caller owns a short, private transaction. Storage never runs while these locks are held.
    private async Task<Snapshot?> LoadAsync(ReviewPromotionRequest request, ReviewAppealPromotionRequest? appeal, Guid generation, CancellationToken cancellationToken)
    {
        if (appeal is not null)
        {
            return await LoadAppealAsync(appeal, generation, cancellationToken);
        }
        var uploads = await db.Database.SqlQuery<Guid>($"select upload_id as \"Value\" from safety.photo_review_cases where id={request.CaseId}")
            .ToListAsync(cancellationToken);
        if (uploads.Count != 1)
        {
            return null;
        }

        var uploadId = uploads[0];
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={uploadId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={request.CaseId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={request.CandidateGeneration} for share", cancellationToken);
        var now = time.GetUtcNow();
        var rows = await db.Database.SqlQuery<CandidateState>($"""
            select u.id as "UploadId",u.owner_id as "OwnerId",u.version as "UploadVersion",r.version as "CaseVersion",
              c.generation as "CandidateGeneration",r.policy_version as "PolicyVersion",c.detected_type as "DetectedType",
              c.width as "Width",c.height as "Height",r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.upload_id=u.id and c.case_id=r.id
            join identity.users owner on owner.id=u.owner_id
            where r.id={request.CaseId} and u.id={uploadId} and r.version={request.CaseVersion} and r.policy_version={request.PolicyVersion}
              and u.version={request.UploadVersion} and u.state='AwaitingReview' and r.version<9223372036854775807
              and c.generation={request.CandidateGeneration} and c.upload_version=u.version-1
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.deleted_at is null
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
              and r.review_deadline>{now} and c.retention_until>{now}
            """).ToListAsync(cancellationToken);
        if (rows.Count != 1)
        {
            return null;
        }

        var candidate = rows[0];
        var variants = await db.Database.SqlQuery<StoredVariant>($"""
            select kind as "Kind",object_key as "ObjectKey",width as "Width",height as "Height",
              size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_candidate_variants where generation={request.CandidateGeneration} order by kind for share
            """).ToListAsync(cancellationToken);
        now = time.GetUtcNow();
        if (candidate.ReviewDeadline <= now || candidate.RetentionUntil <= now)
        {
            return null;
        }

        return Validate(candidate, variants, generation);
    }

    private static Snapshot? Validate(CandidateState state, List<StoredVariant> variants, Guid generation)
    {
        if (variants.Count != 3 || variants.Select(item => item.Kind).Distinct(StringComparer.Ordinal).Count() != 3
            || !PhotoFormats.TryParseContentType(state.DetectedType, out _) || state.Width < 1 || state.Height < 1
            || (long)state.Width * state.Height > 20_000_000)
        {
            return null;
        }

        var verified = new List<Variant>();
        long total = 0;
        foreach (var kind in PhotoVariants.All)
        {
            var row = variants.SingleOrDefault(item => item.Kind == PhotoVariants.Name(kind));
            var source = ObjectKey.ForReviewCandidate(state.CandidateGeneration, kind);
            var size = PhotoVariants.Fit(state.Width, state.Height, PhotoVariants.MaxSide(kind));
            if (row is null || row.ObjectKey != source.Value || (row.Width, row.Height) != size
                || row.SizeBytes is < 1 or > 16_777_216 || row.Sha256 is not { Length: 32 })
            {
                return null;
            }

            total += row.SizeBytes;
            if (total > 25_165_824)
            {
                return null;
            }

            verified.Add(new Variant(kind, source, ObjectKey.ForPromotedDerivative(generation, kind),
                row.Width, row.Height, row.SizeBytes, row.Sha256.ToArray()));
        }

        return new Snapshot(state, [.. verified]);
    }

    private async Task<byte[]?> ReadBytesAsync(Variant variant, CancellationToken cancellationToken)
    {
        var bytes = new byte[checked((int)variant.SizeBytes)];
        var excess = new byte[1];
        var transferred = false;
        try
        {
            await using (var stream = await store.OpenReadAsync(variant.Source, cancellationToken))
            {
                if (stream is null)
                {
                    return null;
                }

                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                    if (read == 0)
                    {
                        return null;
                    }

                    offset += read;
                }

                if (await stream.ReadAsync(excess, cancellationToken) != 0)
                {
                    return null;
                }
            }

            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), variant.Sha256))
            {
                return null;
            }

            transferred = true;
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(excess);
            if (!transferred)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}
