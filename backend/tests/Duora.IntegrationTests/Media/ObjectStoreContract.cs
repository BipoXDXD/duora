using System.Security.Cryptography;
using Duora.Core.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Contrato da porta <see cref="IObjectStore"/> (ADR-0025): a mesma suíte roda contra cada adaptador, para o
/// filesystem de desenvolvimento não esconder diferença de comportamento do S3 de produção.
/// </summary>
public abstract class ObjectStoreContract
{
    protected abstract IObjectStore Store { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrivateReviewCandidateDeletionCannotAffectOriginalsOrPublishedDerivatives()
    {
        var original = ObjectKey.NewQuarantine();
        var published = ObjectKey.ForDerivative(original, PhotoVariant.Card);
        var candidate = ObjectKey.Parse($"review-candidates/{Guid.NewGuid():N}-card");
        await PutAsync(original, [10]);
        await PutAsync(published, [20]);
        await PutAsync(candidate, [30]);
        Assert.Equal([30], await ReadAsync(candidate));

        await Store.DeleteAsync(candidate, Cancellation);

        Assert.Null(await ReadAsync(candidate));
        Assert.Equal([10], await ReadAsync(original));
        Assert.Equal([20], await ReadAsync(published));
    }

    [Fact]
    public async Task DeletingAnOldCandidateGenerationPreservesAllVariantsOfAnotherGeneration()
    {
        var abandoned = Guid.NewGuid();
        var winner = Guid.NewGuid();
        foreach (var variant in PhotoVariants.All)
        {
            await PutAsync(ObjectKey.ForReviewCandidate(abandoned, variant), [10]);
            await PutAsync(ObjectKey.ForReviewCandidate(winner, variant), [20]);
        }

        foreach (var variant in PhotoVariants.All)
        {
            await Store.DeleteAsync(ObjectKey.ForReviewCandidate(abandoned, variant), Cancellation);
        }

        foreach (var variant in PhotoVariants.All)
        {
            Assert.Null(await ReadAsync(ObjectKey.ForReviewCandidate(abandoned, variant)));
            Assert.Equal([20], await ReadAsync(ObjectKey.ForReviewCandidate(winner, variant)));
        }
    }

    [Fact]
    public async Task WrittenBytesAreReadBackExactly()
    {
        var key = ObjectKey.NewQuarantine();
        var bytes = RandomNumberGenerator.GetBytes(4096);

        await PutAsync(key, bytes);

        Assert.Equal(bytes, await ReadAsync(key));
    }

    [Fact]
    public async Task AFiveMebibyteBodyFromANonSeekableStreamIsStored()
    {
        // O corpo da requisição HTTP não é pesquisável: o adaptador não pode depender de Seek nem de Length.
        var key = ObjectKey.NewQuarantine();
        var bytes = RandomNumberGenerator.GetBytes((int)PhotoLimits.MaxBytes);

        await using (var forwardOnly = new ForwardOnlyStream(bytes))
        {
            await Store.PutAsync(key, forwardOnly, bytes.Length, "image/png", Cancellation);
        }

        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await ReadAsync(key) ?? []));
    }

    [Fact]
    public async Task WritingTheSameKeyAgainReplacesTheObject()
    {
        // Retomada do Worker depois de perder o lease grava o mesmo derivado de novo (spec 16.2).
        var key = ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Card);
        await PutAsync(key, [1, 2, 3]);

        await PutAsync(key, [9, 8, 7, 6]);

        Assert.Equal([9, 8, 7, 6], await ReadAsync(key));
    }

    [Fact]
    public async Task AMissingObjectReadsAsNull()
    {
        Assert.Null(await ReadAsync(ObjectKey.NewQuarantine()));
    }

    [Fact]
    public async Task ADeletedObjectIsGone()
    {
        var key = ObjectKey.NewQuarantine();
        await PutAsync(key, [1, 2, 3]);

        await Store.DeleteAsync(key, Cancellation);

        Assert.Null(await ReadAsync(key));
    }

    [Fact]
    public async Task DeletingWhatDoesNotExistSucceeds()
    {
        var key = ObjectKey.NewQuarantine();

        await Store.DeleteAsync(key, Cancellation);
        await Store.DeleteAsync(key, Cancellation);

        Assert.Null(await ReadAsync(key));
    }

    [Fact]
    public async Task AStreamShorterThanTheDeclaredLengthFailsAndLeavesNothingReadable()
    {
        // Cliente que cai no meio do upload: nada parcial pode virar objeto legível.
        var key = ObjectKey.NewQuarantine();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var shortStream = new ForwardOnlyStream(new byte[100]);
            await Store.PutAsync(key, shortStream, 200, "image/png", Cancellation);
        });

        Assert.Null(await ReadAsync(key));
    }

    [Fact]
    public async Task AStreamLongerThanTheDeclaredLengthFailsAndLeavesNothingReadable()
    {
        var key = ObjectKey.NewQuarantine();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var longStream = new ForwardOnlyStream(new byte[300]);
            await Store.PutAsync(key, longStream, 200, "image/png", Cancellation);
        });

        Assert.Null(await ReadAsync(key));
    }

    [Fact]
    public async Task QuarantineAndDerivativesAreSeparateAreas()
    {
        var original = ObjectKey.NewQuarantine();
        var derivative = ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Thumb);
        await PutAsync(original, [1]);
        await PutAsync(derivative, [2]);

        await Store.DeleteAsync(original, Cancellation);

        Assert.Null(await ReadAsync(original));
        Assert.Equal([2], await ReadAsync(derivative));
    }

    protected async Task PutAsync(ObjectKey key, byte[] bytes)
    {
        await using var content = new MemoryStream(bytes, writable: false);
        await Store.PutAsync(key, content, bytes.Length, "application/octet-stream", Cancellation);
    }

    protected async Task<byte[]?> ReadAsync(ObjectKey key)
    {
        await using var stream = await Store.OpenReadAsync(key, Cancellation);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Cancellation);
        return copy.ToArray();
    }
}

/// <summary>Fluxo só de leitura e para frente, como o corpo de uma requisição.</summary>
internal sealed class ForwardOnlyStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

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
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
