using Barrier = Duora.ClosureProbe.Barrier;

namespace Duora.ClosureBoundary;

// Keeps the adapter's actual exact-length EOF probe, temporary, and rename in the protected session.
internal sealed class PausedBody(Stream source, long length, int variant, Barrier barrier) : Stream
{
    private long _remaining = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining == 0)
        {
            barrier.Wait("eof", variant);
        }

        var read = await source.ReadAsync(buffer, cancellationToken);
        _remaining = Math.Max(0, _remaining - read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
