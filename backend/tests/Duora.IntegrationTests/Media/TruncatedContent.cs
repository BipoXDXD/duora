using System.Net;
using System.Net.Http.Headers;

namespace Duora.IntegrationTests.Media;

/// <summary>Declara mais bytes do que manda: o cliente que cai no meio do upload.</summary>
internal sealed class TruncatedContent : HttpContent
{
    private readonly byte[] _bytes;
    private readonly long _declaredLength;

    public TruncatedContent(byte[] bytes, long declaredLength)
    {
        _bytes = bytes;
        _declaredLength = declaredLength;
        Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        Headers.ContentLength = declaredLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_bytes).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _declaredLength;
        return true;
    }
}
