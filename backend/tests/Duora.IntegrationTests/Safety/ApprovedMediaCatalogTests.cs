using System.Data.Common;
using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task ApprovedCatalogSelectsOnlyCommittedApprovedAssetsWithoutReadingFilesOrLeakingKeys()
    {
        var legacy = await CatalogLegacyAsync();
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<Duora.Infrastructure.Safety.PhotoReviewApprovalResult.Approved>(
            await ApproveAsync(reviewer, item, Key()));
        _ = await CaseAsync();
        var before = await ExpiredCleanupDomainAsync();
        var files = Files();
        // Metadata selection must not mistake filesystem availability for SQL eligibility.
        foreach (var file in _root.GetFiles("*", SearchOption.AllDirectories))
        {
            file.Delete();
        }

        var catalog = await Catalog().CaptureAsync(10, 100_000_000, Ct);

        Assert.Equal(before, await ExpiredCleanupDomainAsync());
        Assert.Equal(2, catalog.Assets.Count);
        Assert.Contains(catalog.Assets, a => a.AssetId == legacy);
        var current = Assert.Single(catalog.Assets, a => a.AssetId == approved.AssetId);
        Assert.All(current.Variants, v => Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(TestImages.WebP(64, 32))), v.ExpectedSha256));
        Assert.All(Assert.Single(catalog.Assets, a => a.AssetId == legacy).Variants, v => Assert.Null(v.ExpectedSha256));
        Assert.All(catalog.Assets, a => Assert.Equal(3, a.Variants.Count));
        Assert.Equal(catalog.Assets.Sum(a => a.Variants.Sum(v => v.SizeBytes)), catalog.TotalBytes);
        Assert.NotEmpty(catalog.Snapshot);
        Assert.NotEmpty(files);
        Assert.Equal("ApprovedMediaCatalog", catalog.ToString());
        Assert.All(catalog.Assets.SelectMany(a => a.Variants), v =>
        {
            Assert.Equal(StorageArea.Derivatives, v.Key.Area);
            Assert.DoesNotContain(v.Key.Value, v.ToString(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("missing-variant")]
    [InlineData("foreign-key")]
    [InlineData("wrong-size")]
    [InlineData("wrong-dimensions")]
    public async Task ApprovedCatalogRefusesTheWholeSnapshotForAMalformedPromotedManifest(string mismatch)
    {
        _ = await CatalogLegacyAsync();
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<Duora.Infrastructure.Safety.PhotoReviewApprovalResult.Approved>(
            await ApproveAsync(reviewer, item, Key()));
        var sql = mismatch switch
        {
            "missing-variant" => "delete from media.media_variants where media_asset_id=$1 and kind='thumb'",
            "foreign-key" => "update media.media_variants set object_key='derivatives/0123456789abcdef0123456789abcdef-thumb' where media_asset_id=$1 and kind='thumb'",
            "wrong-size" => "update media.media_variants set size_bytes=size_bytes+1 where media_asset_id=$1 and kind='thumb'",
            _ => "update media.media_variants set width=width+1 where media_asset_id=$1 and kind='thumb'",
        };
        await ExecuteAsync(sql, approved.AssetId);
        var before = await ExpiredCleanupDomainAsync();
        var files = Files();

        await Assert.ThrowsAsync<InvalidDataException>(() => Catalog().CaptureAsync(10, 100_000_000, Ct));

        Assert.Equal(before, await ExpiredCleanupDomainAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("asset-deleted")]
    [InlineData("upload-deleted")]
    [InlineData("owner-divergent")]
    public async Task ApprovedCatalogExcludesAnAssetWithoutItsCurrentlyApprovedMatchingUpload(string mismatch)
    {
        var included = await CatalogLegacyAsync();
        var excluded = await CatalogLegacyAsync();
        if (mismatch == "asset-deleted")
        {
            await ExecuteAsync("update media.media_assets set state='Deleted',deleted_at=$2,version=version+1 where id=$1", excluded, _time.GetUtcNow());
        }
        else if (mismatch == "upload-deleted")
        {
            await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=(select upload_id from media.media_assets where id=$1)", excluded);
        }
        else
        {
            var owner = await ActorAsync(AccountRoles.Participant);
            await ExecuteAsync("update media.media_assets set owner_id=$2 where id=$1", excluded, owner.UserId);
        }
        var catalog = await Catalog().CaptureAsync(10, 100_000_000, Ct);
        Assert.Equal(included, Assert.Single(catalog.Assets).AssetId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovedCatalogRefusesAssetOrByteOverflowInsteadOfReturningAPartialBackup(bool assetLimit)
    {
        _ = await CatalogLegacyAsync();
        _ = await CatalogLegacyAsync();
        var before = await ExpiredCleanupDomainAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Catalog().CaptureAsync(assetLimit ? 1 : 10,
            assetLimit ? 100_000_000 : 1, Ct));
        Assert.Equal(before, await ExpiredCleanupDomainAsync());
    }

    [Fact]
    public async Task ApprovedCatalogKeepsOneReadOnlySnapshotAcrossAConcurrentDeletionWithoutBusinessLocks()
    {
        var asset = await CatalogLegacyAsync();
        var observer = new CatalogConcurrentDeletion(async () =>
        {
            await ExecuteAsync("update media.media_assets set state='Deleted',deleted_at=$2,version=version+1 where id=$1", asset, _time.GetUtcNow());
            await ExecuteAsync("delete from media.media_variants where media_asset_id=$1 and kind='thumb'", asset);
        });

        var catalog = await Catalog(observer).CaptureAsync(10, 100_000_000, Ct);

        Assert.True(observer.Fired);
        Assert.Equal(asset, Assert.Single(catalog.Assets).AssetId);
        Assert.Equal(3, Assert.Single(catalog.Assets).Variants.Count);
        Assert.Empty((await Catalog().CaptureAsync(10, 100_000_000, Ct)).Assets);
    }

    [Fact]
    public async Task ApprovedCatalogRejectsAmbientTransactionsAndObservesCallerCancellation()
    {
        using (var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().CaptureAsync(10, 100_000_000, Ct));
        }
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog().CaptureAsync(10, 100_000_000, cancelled.Token));
    }

    private ApprovedMediaBackupCatalog Catalog(params IInterceptor[] interceptors) => new(Factory(interceptors), _time);

    private async Task<Guid> CatalogLegacyAsync()
    {
        var owner = await ActorAsync(AccountRoles.Participant);
        var now = _time.GetUtcNow();
        var original = TestImages.Jpeg(64, 32);
        var upload = Upload.Reserve(owner.UserId, Key(), PhotoFormat.Jpeg, original.Length, now);
        await using var db = Context();
        db.Add(upload);
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlAsync($"update media.uploads set state='Approved',version=2,content_sha256={SHA256.HashData(original)} where id={upload.Id}", Ct);
        var asset = MediaAsset.Approve(upload.Id, owner.UserId, PhotoFormat.Jpeg, 64, 32, SHA256.HashData(original), now);
        db.Add(asset);
        foreach (var kind in PhotoVariants.All)
        {
            db.Add(MediaVariant.Create(asset.Id, kind, ObjectKey.ForDerivative(ObjectKey.Parse(upload.QuarantineKey), kind),
                64, 32, TestImages.WebP(64, 32).Length, now));
        }
        await db.SaveChangesAsync(Ct);
        return asset.Id;
    }

    private sealed class CatalogConcurrentDeletion(Func<Task> delete) : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("from media.media_assets a", StringComparison.Ordinal))
            {
                var connection = command.Connection ?? throw new InvalidOperationException("A catalog query needs its owned connection.");
                await using var probe = connection.CreateCommand();
                probe.Transaction = command.Transaction;
                probe.CommandText = "select current_setting('transaction_read_only')";
                Assert.Equal("on", await probe.ExecuteScalarAsync(cancellationToken));
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("from media.media_assets a", StringComparison.Ordinal))
            {
                Fired = true;
                Assert.NotNull(command.Transaction);
                Assert.Equal(System.Data.IsolationLevel.RepeatableRead, command.Transaction.IsolationLevel);
                await delete();
            }
            return result;
        }
    }
}
