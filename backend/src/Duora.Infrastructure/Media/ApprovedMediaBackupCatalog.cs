using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Media;

internal sealed record ApprovedMediaCatalogVariant(PhotoVariant Kind, ObjectKey Key, int Width, int Height,
    long SizeBytes, string? ExpectedSha256)
{
    public override string ToString() => nameof(ApprovedMediaCatalogVariant);
}

internal sealed record ApprovedMediaCatalogAsset(Guid AssetId, long Version,
    IReadOnlyList<ApprovedMediaCatalogVariant> Variants)
{
    public Guid PhotoId { get; init; }
    public Guid? PromotionGeneration { get; init; }

    public override string ToString() => nameof(ApprovedMediaCatalogAsset);
}

internal sealed record ApprovedMediaCatalog(DateTimeOffset CapturedAt, string Snapshot,
    IReadOnlyList<ApprovedMediaCatalogAsset> Assets, long TotalBytes)
{
    public override string ToString() => nameof(ApprovedMediaCatalog);
}

/// <summary>Private metadata snapshot only; never an exporter, backup writer or restore admission authority.</summary>
internal sealed class ApprovedMediaBackupCatalog(IDbContextFactory<DuoraDbContext> contexts, TimeProvider time)
{
    private const int MaximumCatalogAssets = 4096;
    private const long MaximumAssetBytes = 24 * 1024 * 1024;
    private static readonly string[] VariantNames = ["card", "full", "thumb"];

    internal async Task<ApprovedMediaCatalog> CaptureAsync(int maxAssets, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAssets, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAssets, MaximumCatalogAssets);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, MaximumAssetBytes * MaximumCatalogAssets);
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("The approved catalog owns its read-only snapshot.");
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("set transaction read only", ct);
        await db.Database.ExecuteSqlRawAsync("set local statement_timeout='15s'", ct);
        var snapshot = await db.Database.SqlQuery<string>($"select pg_current_snapshot()::text as \"Value\"").SingleAsync(ct);
        var capturedAt = time.GetUtcNow();
        var roots = await db.Database.SqlQuery<AssetRoot>($"""
            select a.id as "AssetId",a.version as "Version",a.width as "Width",a.height as "Height",
              u.quarantine_key as "QuarantineKey",p.generation as "Generation",
              coalesce(case when p.generation is null then
                not exists(select 1 from safety.photo_review_cases r where r.upload_id=u.id)
              else p.committed_at is not null and p.writer_settled_at is not null
                and p.cleanup_requested_at is null and p.deleted_at is null
                and p.cleanup_token is null and p.cleanup_lease_until is null
                and p.upload_id=u.id and p.policy_version='human-review-v1' and p.upload_version=u.version-1
                and p.detected_type=a.detected_type and p.width=a.width and p.height=a.height
                and exists(select 1 from safety.photo_review_cases r
                  join safety.photo_review_decisions d on d.case_id=r.id and d.case_version=r.version
                  where r.id=p.case_id and r.upload_id=u.id and r.candidate_generation=p.candidate_generation
                    and r.policy_version=p.policy_version and r.version=p.case_version+1
                    and d.candidate_generation=p.candidate_generation and d.policy_version=p.policy_version
                    and d.decision='approve' and d.reason_code='meets_policy'
                    and ((d.appeal_id is null and p.appeal_id is null and p.appeal_version is null
                      and exists(select 1 from safety.photo_review_decision_commands receipt
                        where receipt.case_id=r.id and receipt.actor_id=d.actor_id and receipt.result_version=r.version
                          and receipt.result_asset_id=a.id and receipt.result_photo_version=u.version
                          and receipt.operation='decide' and receipt.result_decision='approve')
                      and exists(select 1 from ops.audit_events e where e.target_id=d.id
                        and e.target_type='photo_review_decision' and e.target_version=d.case_version
                        and e.action='safety.photo_review_approved' and e.actor_id=d.actor_id))
                    or (d.appeal_id=p.appeal_id
                      and exists(select 1 from safety.photo_review_appeals appeal
                        join safety.photo_review_decisions original on original.id=appeal.original_decision_id
                        join safety.photo_review_appeal_decision_commands receipt on receipt.appeal_id=appeal.id
                        where appeal.id=d.appeal_id and appeal.case_id=r.id and appeal.owner_id=u.owner_id
                          and appeal.candidate_generation=p.candidate_generation and appeal.policy_version=p.policy_version
                          and appeal.status='ClosedApproved' and appeal.version=p.appeal_version+1
                          and appeal.closed_at=d.decided_at and d.decided_at<appeal.maximum_retention_until
                          and appeal.maximum_retention_until=original.decided_at+interval '720 hours'
                          and appeal.original_rejected_at=original.decided_at
                          and original.id=d.original_decision_id and original.appeal_id is null
                          and original.case_id=r.id and original.case_version=r.version-2
                          and original.candidate_generation=p.candidate_generation and original.policy_version=p.policy_version
                          and original.actor_id<>d.actor_id and original.decision='reject'
                          and original.reason_code='does_not_meet_policy'
                          and d.candidate_retention_until_snapshot=least(d.decided_at+interval '24 hours',appeal.maximum_retention_until)
                          and receipt.decision_id=d.id and receipt.actor_id=d.actor_id and receipt.case_id=r.id
                          and receipt.result_appeal_version=appeal.version and receipt.result_case_version=r.version
                          and receipt.result_photo_version=u.version and receipt.result_asset_id=a.id and receipt.result_decision='approve')
                      and exists(select 1 from ops.audit_events e where e.target_id=d.id
                        and e.target_type='photo_review_decision' and e.target_version=d.case_version
                        and e.action='safety.photo_review_appeal_approved' and e.actor_id=d.actor_id))))
              end,false) as "BindingValid"
            from media.media_assets a join media.uploads u on u.id=a.upload_id
            left join media.review_promotions p on p.asset_id=a.id
            where a.state='Approved' and a.deleted_at is null and a.approved_at is not null
              and u.state='Approved' and u.owner_id=a.owner_id
            order by a.id limit {maxAssets + 1}
            """).ToListAsync(ct);
        if (roots.Count > maxAssets || roots.Any(a => !a.BindingValid || a.AssetId == Guid.Empty || a.Version < 1
                || a.Width < 1 || a.Height < 1 || (long)a.Width * a.Height > 20_000_000))
        {
            throw Inconsistent();
        }
        if (roots.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return new ApprovedMediaCatalog(capturedAt, snapshot, Array.Empty<ApprovedMediaCatalogAsset>(), 0);
        }
        var ids = roots.Select(a => a.AssetId).ToArray();
        var variants = await db.Database.SqlQuery<StoredVariant>($"""
            select v.media_asset_id as "AssetId",v.kind as "Kind",v.object_key as "ObjectKey",
              v.width as "Width",v.height as "Height",v.size_bytes as "SizeBytes",
              pv.object_key as "PromotionKey",pv.width as "PromotionWidth",pv.height as "PromotionHeight",
              pv.size_bytes as "PromotionSize",pv.sha256 as "PromotionSha256",
              dv.size_bytes as "DecisionSize",dv.sha256 as "DecisionSha256",d.appeal_id as "AppealId",
              original.size_bytes as "OriginalSize",original.sha256 as "OriginalSha256"
            from media.media_variants v
            left join media.review_promotions p on p.asset_id=v.media_asset_id
            left join media.review_promotion_variants pv on pv.generation=p.generation and pv.kind=v.kind
            left join safety.photo_review_decisions d on d.case_id=p.case_id and d.case_version=p.case_version+1
              and d.decision='approve'
            left join safety.photo_review_decision_variants dv on dv.decision_id=d.id and dv.variant=v.kind
            left join safety.photo_review_decision_variants original on original.decision_id=d.original_decision_id and original.variant=v.kind
            where v.media_asset_id=any({ids}) order by v.media_asset_id,v.kind limit {3 * maxAssets + 1}
            """).ToListAsync(ct);
        if (variants.Count > 3 * maxAssets)
        {
            throw Inconsistent();
        }
        var byAsset = variants.ToLookup(v => v.AssetId);
        var catalog = new List<ApprovedMediaCatalogAsset>(roots.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var root in roots)
        {
            var stored = byAsset[root.AssetId].ToArray();
            if (stored.Length != 3 || !stored.Select(v => v.Kind).SequenceEqual(VariantNames))
            {
                throw Inconsistent();
            }
            var manifest = new List<ApprovedMediaCatalogVariant>(3);
            long assetBytes = 0;
            foreach (var variant in stored)
            {
                var kind = variant.Kind switch
                {
                    "thumb" => PhotoVariant.Thumb,
                    "card" => PhotoVariant.Card,
                    "full" => PhotoVariant.Full,
                    _ => throw Inconsistent(),
                };
                ObjectKey expected;
                try
                {
                    expected = root.Generation is { } generation
                        ? ObjectKey.ForPromotedDerivative(generation, kind)
                        : ObjectKey.ForDerivative(ObjectKey.Parse(root.QuarantineKey), kind);
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException)
                {
                    throw Inconsistent();
                }
                var dimensions = PhotoVariants.Fit(root.Width, root.Height, PhotoVariants.MaxSide(kind));
                if (variant.ObjectKey != expected.Value || !keys.Add(variant.ObjectKey)
                    || variant.Width != dimensions.Width || variant.Height != dimensions.Height
                    || variant.SizeBytes is < 1 or > 16_777_216)
                {
                    throw Inconsistent();
                }
                string? hash = null;
                if (root.Generation is not null)
                {
                    if (variant.PromotionKey != variant.ObjectKey || variant.PromotionWidth != variant.Width
                        || variant.PromotionHeight != variant.Height || variant.PromotionSize != variant.SizeBytes
                        || variant.DecisionSize != variant.SizeBytes || variant.PromotionSha256 is not { Length: 32 } promotionHash
                        || variant.DecisionSha256 is not { Length: 32 } decisionHash
                        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(promotionHash, decisionHash))
                    {
                        throw Inconsistent();
                    }
                    if (variant.AppealId is not null && (variant.OriginalSize != variant.SizeBytes
                        || variant.OriginalSha256 is not { Length: 32 } originalHash
                        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(originalHash, decisionHash)))
                    {
                        throw Inconsistent();
                    }
                    hash = Convert.ToHexStringLower(promotionHash);
                }
                // Legacy has no persisted derivative hash. An original hash is never a substitute.
                manifest.Add(new ApprovedMediaCatalogVariant(kind, expected, variant.Width, variant.Height, variant.SizeBytes, hash));
                assetBytes = checked(assetBytes + variant.SizeBytes);
            }
            total = checked(total + assetBytes);
            if (assetBytes > MaximumAssetBytes || total > maxBytes)
            {
                throw Inconsistent();
            }
            catalog.Add(new ApprovedMediaCatalogAsset(root.AssetId, root.Version, manifest.AsReadOnly()));
        }
        await transaction.CommitAsync(ct);
        return new ApprovedMediaCatalog(capturedAt, snapshot, catalog.AsReadOnly(), total);
    }

    private static InvalidDataException Inconsistent() => new("The complete approved catalog could not be established.");

    private sealed record AssetRoot(Guid AssetId, long Version, int Width, int Height, string QuarantineKey,
        Guid? Generation, bool BindingValid);

    private sealed record StoredVariant(Guid AssetId, string Kind, string ObjectKey, int Width, int Height, long SizeBytes,
        string? PromotionKey, int? PromotionWidth, int? PromotionHeight, long? PromotionSize, byte[]? PromotionSha256,
        long? DecisionSize, byte[]? DecisionSha256, Guid? AppealId, long? OriginalSize, byte[]? OriginalSha256);
}
