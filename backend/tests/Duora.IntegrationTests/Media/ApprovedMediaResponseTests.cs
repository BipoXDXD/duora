using Duora.Infrastructure.Media;
using Duora.Web.Media;
using Microsoft.AspNetCore.Http;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// A resposta de API-M04 sem servidor HTTP: o corpo vai em fatias de 64 KiB, para o Kestrel não segurar uma segunda
/// cópia inteira do derivado no pipe.
/// </summary>
public sealed class ApprovedMediaResponseTests
{
    private const int Chunk = 64 * 1024;

    [Fact]
    public async Task ABufferedDerivativeIsWrittenInChunksOfAtMost64KiB()
    {
        var bytes = Enumerable.Range(0, 5 * 1024 * 1024).Select(i => (byte)i).ToArray();
        var body = new RecordingBody();
        var http = new DefaultHttpContext();
        http.Response.Body = body;

        await new MediaAssetEndpoints.ApprovedMediaResult(new AuthorizedMedia(new MemoryStream(bytes))).ExecuteAsync(http);

        Assert.Equal(bytes, body.ToArray());
        Assert.InRange(body.LargestWrite, 1, Chunk);
        Assert.True(body.Flushes >= bytes.Length / Chunk, $"flushes={body.Flushes}");
    }

    private sealed class RecordingBody : MemoryStream
    {
        internal int LargestWrite { get; private set; }

        internal int Flushes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
