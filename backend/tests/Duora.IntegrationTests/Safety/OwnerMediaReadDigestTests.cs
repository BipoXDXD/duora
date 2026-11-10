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

    /// <summary>
    /// A regra de legado vale só para asset sem promoção: um asset promovido cuja chave passou a ter o formato legado
    /// não perde a conferência.
    /// </summary>
    [Fact]
    public async Task ALegacyFormatKeyOnAPromotedAssetIsRefused()
    {
        var read = await PromotedReadAsync();
        var legacy = ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Full);
        byte[] other = [.. TestImages.WebP(64, 32).Select(value => (byte)(value ^ 0x5a))];
        await new FileSystemObjectStore(_root.FullName).PutAsync(legacy, new MemoryStream(other), other.Length,
            "image/webp", Ct);
        await ExecuteAsync("update media.media_variants set object_key=$2 where media_asset_id=$1 and kind='full'",
            read.AssetId, legacy.Value);

        var media = await read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId,
            PhotoVariant.Full, Ct);

        Assert.Null(media);
        var entry = Assert.Single(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain(legacy.Value, entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Um objeto maior que o tamanho gravado não pode ser lido inteiro para a memória: a leitura para no byte que
    /// passa do tamanho. O storage aqui falha se alguém pedir além de <c>size_bytes + 1</c>.
    /// </summary>
    [Fact]
    public async Task AnOversizedObjectIsNeverReadPastOneByteBeyondItsRecordedSize()
    {
        var limit = 0L;
        var guarded = new GuardedStore(new FileSystemObjectStore(_root.FullName), () => limit);
        var read = await PromotedReadAsync(guarded);
        var path = Path.Combine(_root.FullName, read.Key.Value);
        var original = await File.ReadAllBytesAsync(path, Ct);
        limit = original.Length + 1;
        await File.WriteAllBytesAsync(path, [.. original, .. new byte[1024 * 1024]], Ct);

        var media = await read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId,
            PhotoVariant.Full, Ct);

        Assert.Null(media);
        Assert.False(guarded.Overread, "A leitura passou de size_bytes + 1.");
    }

    private async Task<PromotedRead> PromotedReadAsync(IObjectStore? store = null)
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
        var reads = new OwnerMediaReads(Factory(), store ?? new FileSystemObjectStore(_root.FullName), _time,
            factory.CreateLogger<OwnerMediaReads>());
        return new PromotedRead(reads, owner, approved.AssetId, ObjectKey.Parse(key), logs);
    }

    private sealed record PromotedRead(OwnerMediaReads Reads, PhotoReviewActor Owner, Guid AssetId, ObjectKey Key,
        CapturingLoggerProvider Logs);

    /// <summary>Entrega o objeto real, mas marca e recusa qualquer leitura que passe do limite.</summary>
    private sealed class GuardedStore(IObjectStore inner, Func<long> limit) : IObjectStore
    {
        internal bool Overread { get; private set; }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType,
            CancellationToken cancellationToken) => inner.PutAsync(key, content, length, contentType, cancellationToken);

        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            await inner.OpenReadAsync(key, cancellationToken) is { } stream
                ? new GuardedStream(stream, limit(), () => Overread = true)
                : null;

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class GuardedStream(Stream inner, long limit, Action overread) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _position += read;
            if (_position > limit)
            {
                overread();
                throw new InvalidOperationException("Leitura além de size_bytes + 1.");
            }

            return read;
        }
    }
}
