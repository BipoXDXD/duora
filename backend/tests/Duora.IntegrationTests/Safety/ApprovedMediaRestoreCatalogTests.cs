using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task ARealApprovedCatalogCanBeSelectedButALaterLogicalTombstoneRemovesItsBytes()
    {
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var catalog = await Catalog().CaptureAsync(10, 100_000_000, Ct);
        var before = await ExpiredCleanupDomainAsync();
        var files = Files();

        var selected = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(
            ApprovedMediaRestoreSelectionTests.Select(ApprovedMediaRestoreSelectionTests.FromCatalog(catalog)));
        Assert.Equal(approved.AssetId, Assert.Single(selected.Assets).AssetId);
        Assert.Equal(item.UploadId, Assert.Single(selected.Assets).PhotoId);
        Assert.Equal(3, Assert.Single(selected.Assets).Variants.Count);

        var excluded = Assert.IsType<ApprovedMediaRestoreSelectionResult.Selected>(
            ApprovedMediaRestoreSelectionTests.Select(ApprovedMediaRestoreSelectionTests.FromCatalog(catalog, item.UploadId)));
        Assert.Empty(excluded.Assets);
        Assert.Equal(0, excluded.TotalBytes);
        Assert.Equal(before, await ExpiredCleanupDomainAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task ApprovedRestoreCatalogBindsTheLogicalPhotoAndCommittedPromotionGeneration()
    {
        var legacyId = await CatalogLegacyAsync();
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        await using var db = Context();
        var legacyPhotoId = await db.Database.SqlQuery<Guid>($"select upload_id as \"Value\" from media.media_assets where id={legacyId}").SingleAsync(Ct);
        var generation = await db.Database.SqlQuery<Guid>($"select generation as \"Value\" from media.review_promotions where asset_id={approved.AssetId}").SingleAsync(Ct);
        var before = await ExpiredCleanupDomainAsync();
        var files = Files();

        var catalog = await Catalog().CaptureAsync(10, 100_000_000, Ct);

        var human = Assert.Single(catalog.Assets, a => a.AssetId == approved.AssetId);
        Assert.Equal(item.UploadId, human.PhotoId);
        Assert.Equal(generation, human.PromotionGeneration);
        var legacy = Assert.Single(catalog.Assets, a => a.AssetId == legacyId);
        Assert.Equal(legacyPhotoId, legacy.PhotoId);
        Assert.Null(legacy.PromotionGeneration);
        Assert.All(human.Variants, v => Assert.Equal(ObjectKey.ForPromotedDerivative(generation, v.Kind), v.Key));
        Assert.Equal(before, await ExpiredCleanupDomainAsync());
        Assert.Equal(files, Files());
    }
}
