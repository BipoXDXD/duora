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
    [InlineData("extended")]
    public async Task APromotedDerivativeWhoseBytesNoLongerMatchItsDigestIsRefusedAndRecorded(string tampering)
    {
        var read = await PromotedReadAsync();
        var path = Path.Combine(_root.FullName, read.Key.Value);
        var original = await File.ReadAllBytesAsync(path, Ct);
        byte[] tampered = tampering == "flipped" ? [.. original[..^1], (byte)(original[^1] ^ 1)] : [.. original, 0];
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
    /// Um corpo que termina antes do tamanho gravado é o que o S3 entrega quando a rede corta a leitura: falha
    /// transitória, que sobe como <see cref="IOException"/> para a admissão de mídia responder 503, sem log de
    /// integridade e sem bytes.
    /// </summary>
    [Fact]
    public async Task ABodyThatEndsBeforeItsRecordedSizeIsATransientFailureNotA404()
    {
        var read = await PromotedReadAsync();
        var path = Path.Combine(_root.FullName, read.Key.Value);
        var original = await File.ReadAllBytesAsync(path, Ct);
        await File.WriteAllBytesAsync(path, original[..^1], Ct);

        await Assert.ThrowsAsync<EndOfStreamException>(() => read.Reads.OpenAsync(read.Owner.UserId,
            read.Owner.SessionId, read.AssetId, PhotoVariant.Full, Ct));

        Assert.DoesNotContain(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
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

    /// <summary>
    /// O buffer de um derivado promovido fica preso enquanto o cliente baixa. Sem espaço no orçamento, a leitura
    /// falha na hora, antes de alocar, com a <see cref="IOException"/> que a admissão de mídia responde com 503.
    /// </summary>
    [Fact]
    public async Task AFullReadBudgetFailsFastWithoutHandingOutBytes()
    {
        var read = await PromotedReadAsync(budget: new OwnerMediaReadBudget(TestImages.WebP(64, 32).Length));
        await using var held = await OpenFullAsync(read);
        Assert.NotNull(held);

        var refused = await Assert.ThrowsAsync<OwnerMediaReadBudgetExhaustedException>(() => OpenFullAsync(read));

        Assert.IsAssignableFrom<IOException>(refused);
        var entry = Assert.Single(read.Logs.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(read.Key.Value, entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReadBudgetComesBackWhenTheResponseIsDisposed()
    {
        var read = await PromotedReadAsync(budget: new OwnerMediaReadBudget(TestImages.WebP(64, 32).Length));
        var first = await OpenFullAsync(read);
        Assert.NotNull(first);
        await first.DisposeAsync();

        await using var second = await OpenFullAsync(read);

        Assert.NotNull(second);
    }

    /// <summary>Recusa por integridade e corpo cortado também devolvem a reserva: nenhum caminho de falha a retém.</summary>
    [Theory]
    [InlineData("flipped", null)]
    [InlineData("truncated", typeof(EndOfStreamException))]
    public async Task TheReadBudgetComesBackWhenTheReadFails(string tampering, Type? failure)
    {
        var read = await PromotedReadAsync(budget: new OwnerMediaReadBudget(TestImages.WebP(64, 32).Length));
        var path = Path.Combine(_root.FullName, read.Key.Value);
        var original = await File.ReadAllBytesAsync(path, Ct);
        await File.WriteAllBytesAsync(path,
            tampering == "flipped" ? [.. original[..^1], (byte)(original[^1] ^ 1)] : original[..^1], Ct);
        AuthorizedMedia? refused = null;
        var error = await Record.ExceptionAsync(async () => refused = await OpenFullAsync(read));
        Assert.Equal(failure, error?.GetType());
        Assert.Null(refused);
        await File.WriteAllBytesAsync(path, original, Ct);

        await using var media = await OpenFullAsync(read);

        Assert.NotNull(media);
    }

    /// <summary>
    /// Uma conta não prende o orçamento das outras: com duas leituras abertas, a terceira dela recebe 503, e outra conta
    /// continua lendo do mesmo orçamento.
    /// </summary>
    [Fact]
    public async Task AThirdOpenReadOfOneAccountIsRefusedWhileAnotherAccountStillReads()
    {
        var budget = new OwnerMediaReadBudget(OwnerMediaReadBudget.DefaultCapacityBytes);
        var greedy = await PromotedReadAsync(budget: budget);
        var other = await PromotedReadAsync(budget: budget);
        await using var first = await OpenFullAsync(greedy);
        await using var second = await OpenFullAsync(greedy);
        Assert.NotNull(first);
        Assert.NotNull(second);

        await Assert.ThrowsAsync<OwnerMediaReadBudgetExhaustedException>(() => OpenFullAsync(greedy));
        await using var others = await OpenFullAsync(other);

        Assert.NotNull(others);
    }

    /// <summary>O limite é da conta, não da foto: duas leituras abertas em fotos diferentes já esgotam a conta.</summary>
    [Fact]
    public async Task TwoOpenReadsOfDifferentAssetsExhaustTheAccount()
    {
        var budget = new OwnerMediaReadBudget(OwnerMediaReadBudget.DefaultCapacityBytes);
        var firstPhoto = await PromotedReadAsync(budget: budget);
        var secondPhoto = await PromotedReadAsync(budget: budget, ownerId: firstPhoto.Owner.UserId);
        Assert.NotEqual(firstPhoto.AssetId, secondPhoto.AssetId);
        await using var first = await OpenFullAsync(firstPhoto);
        await using var second = await OpenFullAsync(secondPhoto);
        Assert.NotNull(first);
        Assert.NotNull(second);

        await Assert.ThrowsAsync<OwnerMediaReadBudgetExhaustedException>(() => OpenFullAsync(firstPhoto));
    }

    /// <summary>Descartar devolve a vaga da conta: leituras em sequência nunca se acumulam contra o limite de duas.</summary>
    [Fact]
    public async Task AnAccountCanReadAgainAndAgainOnceEachReadIsDisposed()
    {
        var read = await PromotedReadAsync(budget: new OwnerMediaReadBudget(OwnerMediaReadBudget.DefaultCapacityBytes));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var media = await OpenFullAsync(read);
            Assert.NotNull(media);
        }
    }

    /// <summary>
    /// Um cliente lento não segura a reserva além do prazo de 60 s: vencido, a reserva volta, a cópia em andamento é
    /// cancelada e a próxima leitura passa. Um tick antes, a reserva ainda está presa.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AReservationPastItsDeadlineIsReleasedAndCancelsTheCopy(long ticksShort, bool released)
    {
        var read = await PromotedReadAsync(budget: new OwnerMediaReadBudget(TestImages.WebP(64, 32).Length));
        await using var slow = await OpenFullAsync(read);
        Assert.NotNull(slow);

        _time.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(ticksShort));

        Assert.Equal(released, slow.Expired.IsCancellationRequested);
        var next = await Record.ExceptionAsync(async () =>
        {
            await using var media = await OpenFullAsync(read);
            Assert.NotNull(media);
        });
        Assert.Equal(released ? null : typeof(OwnerMediaReadBudgetExhaustedException), next?.GetType());
    }

    private static Task<AuthorizedMedia?> OpenFullAsync(PromotedRead read) =>
        read.Reads.OpenAsync(read.Owner.UserId, read.Owner.SessionId, read.AssetId, PhotoVariant.Full, Ct);

    private async Task<PromotedRead> PromotedReadAsync(IObjectStore? store = null, OwnerMediaReadBudget? budget = null,
        Guid? ownerId = null)
    {
        var (reviewer, item) = ownerId is { } existing ? await PreparedForAsync(existing) : await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var owner = await SessionAsync(item.OwnerId, "Normal");
        await using var db = Context();
        var key = await db.Database.SqlQuery<string>($"""
            select object_key as "Value" from media.media_variants where media_asset_id={approved.AssetId} and kind='full'
            """).SingleAsync(Ct);
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var reads = new OwnerMediaReads(Factory(), store ?? new FileSystemObjectStore(_root.FullName), _time,
            budget ?? new OwnerMediaReadBudget(OwnerMediaReadBudget.DefaultCapacityBytes),
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
