using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-M04 sobre um derivado promovido de verdade: a leitura do dono confere o tamanho e o SHA-256 gravados em
/// <c>media.review_promotion_variants</c> antes do primeiro byte, como o host privado já faz do lado dele.
/// </summary>
public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task TheOwnerReadsAPromotedDerivativeWhoseDigestMatches()
    {
        var read = await PromotedReadAsync();

        await using var media = await read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId,
            PhotoVariant.Full, Ct);

        Assert.NotNull(media);
        using var content = new MemoryStream();
        await media.Content.CopyToAsync(content, Ct);
        Assert.Equal(TestImages.WebP(64, 32), content.ToArray());
        Assert.DoesNotContain(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Theory]
    [InlineData("flipped")]
    [InlineData("truncated")]
    [InlineData("extended")]
    public async Task APromotedDerivativeWhoseBytesNoLongerMatchItsDigestIsRefusedAndRecorded(string tampering)
    {
        var read = await PromotedReadAsync();
        var path = Path.Combine(_root.FullName, read.Key.Value);
        var original = await File.ReadAllBytesAsync(path, Ct);
        byte[] tampered = tampering switch
        {
            "flipped" => [.. original[..^1], (byte)(original[^1] ^ 1)],
            "truncated" => original[..^1],
            _ => [.. original, 0],
        };
        await File.WriteAllBytesAsync(path, tampered, Ct);

        var media = await read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId,
            PhotoVariant.Full, Ct);

        Assert.Null(media);
        var entry = Assert.Single(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(read.AssetId.ToString(), entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(read.Key.Value, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(read.Owner.UserId.ToString(), entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Uma chave promovida sem a linha de digest da própria geração não é legado: é um vínculo quebrado, e passar sem
    /// conferir seria o mesmo que não ter a conferência.
    /// </summary>
    [Fact]
    public async Task APromotedKeyWithoutItsDigestRowIsRefused()
    {
        var read = await PromotedReadAsync();
        var unbound = ObjectKey.ForPromotedDerivative(Guid.NewGuid(), PhotoVariant.Full);
        var bytes = TestImages.WebP(64, 32);
        await new FileSystemObjectStore(_root.FullName).PutAsync(unbound, new MemoryStream(bytes), bytes.Length,
            "image/webp", Ct);
        await ExecuteAsync("update media.media_variants set object_key=$2 where media_asset_id=$1 and kind='full'",
            read.AssetId, unbound.Value);

        var media = await read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId,
            PhotoVariant.Full, Ct);

        Assert.Null(media);
        var entry = Assert.Single(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain(unbound.Value, entry.Text, StringComparison.Ordinal);
    }

    private async Task<PromotedRead> PromotedReadAsync()
    {
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var owner = await SessionAsync(item.OwnerId, "Normal");
        await using var db = Context();
        var key = await db.Database.SqlQuery<string>($"""
            select object_key as "Value" from media.media_variants where media_asset_id={approved.AssetId} and kind='full'
            """).SingleAsync(Ct);
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var reads = new OwnerMediaReads(Factory(), new FileSystemObjectStore(_root.FullName), _time,
            factory.CreateLogger<OwnerMediaReads>());
        return new PromotedRead(reads, owner, approved.AssetId, ObjectKey.Parse(key), logs);
    }

    private sealed record PromotedRead(OwnerMediaReads Reads, PhotoReviewActor Owner, Guid AssetId, ObjectKey Key,
        CapturingLoggerProvider Logs);
}
