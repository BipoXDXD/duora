namespace Duora.Infrastructure.Media;

/// <summary>
/// Repassa exatamente <c>length</c> bytes do fluxo de origem e falha se ele terminar antes ou tiver mais que isso.
/// É o que impede o storage de guardar um upload truncado como se estivesse completo (ADR-0025).
/// </summary>
internal sealed class ExactLengthStream : Stream
{
    private readonly Stream _source;
    private readonly long _length;
    private long _remaining;
    private bool _verifiedEnd;

    public ExactLengthStream(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _source = source;
        _length = length;
        _remaining = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0)
        {
            Span<byte> probe = stackalloc byte[1];
            EnsureSourceEnded(_verifiedEnd || _source.Read(probe) == 0);
            return 0;
        }

        return Advance(_source.Read(buffer[..Wanted(buffer.Length)]));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining == 0)
        {
            EnsureSourceEnded(_verifiedEnd || await _source.ReadAsync(new byte[1], cancellationToken) == 0);
            return 0;
        }

        return Advance(await _source.ReadAsync(buffer[..Wanted(buffer.Length)], cancellationToken));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Wanted(int bufferLength) => (int)Math.Min(bufferLength, _remaining);

    private int Advance(int read)
    {
        if (read == 0)
        {
            throw new EndOfStreamException("O conteúdo terminou antes do tamanho declarado.");
        }

        _remaining -= read;
        return read;
    }

    private void EnsureSourceEnded(bool ended)
    {
        if (!ended)
        {
            throw new InvalidDataException("O conteúdo passou do tamanho declarado.");
        }

        _verifiedEnd = true;
    }
}
