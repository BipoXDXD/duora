using System.Buffers.Binary;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.MediaProtocol;

namespace Duora.UnitTests.Media;

public sealed class SanitizerProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static SanitizerRequest Request => new(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        PhotoFormat.Jpeg, [0xff, 0xd8, 0xff], ProtocolBudget.Candidate);

    [Fact]
    public async Task RequestMatchesIndependentWireVectorAndSurvivesFragmentedReads()
    {
        var request = Request;
        var vector = RequestVector(request);
        await using var output = new MemoryStream();
        await SanitizerProtocol.WriteRequestAsync(output, request, Ct);
        Assert.Equal(vector, output.ToArray());
        await using var input = new FragmentedStream(vector);
        var actual = await SanitizerProtocol.ReadRequestAsync(input, Ct);
        Assert.Equal(request.Nonce, actual.Nonce);
        Assert.Equal(request.Format, actual.Format);
        Assert.Equal(request.Content, actual.Content);
        Assert.Equal(request.Budget, actual.Budget);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5 * 1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidRequestLengthIsRefusedWithoutReadingPayload(int length)
    {
        var bytes = RequestVector(Request)[..74];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), length);
        await using var input = new HeaderOnlyStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadRequestAsync(input, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(21)]
    [InlineData(26)]
    [InlineData(58)]
    [InlineData(62)]
    [InlineData(66)]
    [InlineData(70)]
    public async Task UnknownVersionFormatIdentityOrExpandedBudgetsAreRefused(int offset)
    {
        var bytes = RequestVector(Request);
        bytes[offset] = 255;
        await using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadRequestAsync(input, Ct));
    }

    [Fact]
    public async Task EveryTruncationAndExtraByteIsRefused()
    {
        var bytes = RequestVector(Request);
        for (var length = 0; length < bytes.Length; length++)
        {
            await using var input = new MemoryStream(bytes[..length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadRequestAsync(input, Ct));
        }
        await using var extra = new MemoryStream([.. bytes, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadRequestAsync(extra, Ct));
    }

    [Theory]
    [InlineData(PhotoRejection.UnsupportedFormat, 1)]
    [InlineData(PhotoRejection.TypeMismatch, 2)]
    [InlineData(PhotoRejection.TooManyPixels, 3)]
    [InlineData(PhotoRejection.Animated, 4)]
    [InlineData(PhotoRejection.CorruptImage, 5)]
    [InlineData(PhotoRejection.ProcessingLimit, 6)]
    public async Task RejectionCodesHaveExplicitWireValues(PhotoRejection reason, byte code)
    {
        var request = Request;
        var vector = RejectionVector(request, code);
        await using var input = new FragmentedStream(vector);
        Assert.Equal(new SanitizeOutcome.Rejected(reason), await SanitizerProtocol.ReadResponseAsync(input, request, Ct));
        await using var output = new MemoryStream();
        await SanitizerProtocol.WriteResponseAsync(output, request, new SanitizeOutcome.Rejected(reason), Ct);
        Assert.Equal(vector, output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(21)]
    [InlineData(53)]
    [InlineData(54)]
    public async Task InvalidResponseIdentityAndDiscriminatorsAreRefused(int offset)
    {
        var request = Request;
        var bytes = RejectionVector(request, 1);
        bytes[offset] = 255;
        await using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, request, Ct));
    }

    [Fact]
    public async Task StderrIsDiscardedWithinBudgetAndExcessIsRejected()
    {
        await using var exact = new FragmentedStream(new byte[32]);
        await SanitizerProtocol.DrainStderrAsync(exact, 32, Ct);
        await using var excess = new FragmentedStream(new byte[33]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.DrainStderrAsync(excess, 32, Ct));
    }

    [Fact]
    public async Task AProducerKeepingThePipeOpenCannotPreventCallerCancellation()
    {
        using var requestTimeout = new CancellationTokenSource();
        await using var request = new OpenTailStream(RequestVector(Request), requestTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SanitizerProtocol.ReadRequestAsync(request, requestTimeout.Token));
        using var responseTimeout = new CancellationTokenSource();
        await using var response = new OpenTailStream(SuccessVector(Request), responseTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SanitizerProtocol.ReadResponseAsync(response, Request, responseTimeout.Token));
    }

    [Fact]
    public async Task InvalidResultsAreRejectedBeforeWritingAnyResponseBytes()
    {
        var variants = PhotoVariants.All.Select(kind => new SanitizedVariant(kind, WebpHeader(), 40, 20)).ToArray();
        SanitizeOutcome[] invalid =
        [
            new SanitizeOutcome.Rejected((PhotoRejection)999),
            new SanitizeOutcome.Sanitized(PhotoFormat.Png, 40, 20, variants),
            new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 40, 20, variants[..2]),
            new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 41, 20, variants),
            new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 40, 20, [variants[0], variants[0], variants[2]]),
        ];
        foreach (var outcome in invalid)
        {
            await using var output = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.WriteResponseAsync(output, Request, outcome, Ct));
            Assert.Equal(0, output.Length);
        }
    }

    [Fact]
    public async Task SuccessRequiresAllThreeOrderedFramesAndMatchesIndependentVector()
    {
        var request = Request;
        var vector = SuccessVector(request);
        await using var input = new FragmentedStream(vector);
        var actual = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizerProtocol.ReadResponseAsync(input, request, Ct));
        Assert.Equal(PhotoFormat.Jpeg, actual.Detected);
        Assert.Equal(40, actual.Width);
        Assert.Equal(20, actual.Height);
        Assert.Equal(PhotoVariants.All, actual.Variants.Select(v => v.Kind));
        Assert.All(actual.Variants, v => Assert.Equal(WebpHeader(), v.Content));
        await using var output = new MemoryStream();
        await SanitizerProtocol.WriteResponseAsync(output, request, actual, Ct);
        Assert.Equal(vector, output.ToArray());
    }

    [Theory]
    [InlineData(54)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(69)]
    [InlineData(77)]
    [InlineData(109)]
    [InlineData(121)]
    public async Task InvalidSuccessMetadataHashOrContentIsRefused(int offset)
    {
        var request = Request;
        var bytes = SuccessVector(request);
        bytes[offset] = 255;
        await using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, request, Ct));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16 * 1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidFrameLengthIsRefusedBeforeReadingItsPayload(int length)
    {
        var request = Request;
        var bytes = SuccessVector(request)[..109];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(73), length);
        await using var input = new HeaderOnlyStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, request, Ct));
    }

    [Fact]
    public async Task ResponseCannotExceedTheRestrictedTotalBudgetOrContainTrailingData()
    {
        var request = Request with { Budget = ProtocolBudget.Candidate with { TotalBytes = 95 } };
        await using var excessive = new MemoryStream(SuccessVector(request));
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(excessive, request, Ct));
        await using var extra = new MemoryStream([.. SuccessVector(Request), 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(extra, Request, Ct));
    }

    [Fact]
    public async Task AllResponseTruncationsAreRejected()
    {
        var request = Request;
        var bytes = SuccessVector(request);
        for (var length = 0; length < bytes.Length; length++)
        {
            await using var input = new MemoryStream(bytes[..length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, request, Ct));
        }
    }

    // Coherent RIFF/VP8 headers, without a decodable entropy stream. Full images use the integration corpus.
    private static byte[] WebpHeader() =>
        [.. "RIFF"u8, 24, 0, 0, 0, .. "WEBPVP8 "u8, 12, 0, 0, 0, 16, 0, 0, 0x9d, 1, 0x2a, 40, 0, 20, 0, 0, 0];

    [Fact]
    public async Task ExtendedAlphaEnvelopePreservesAllThreeFrames()
    {
        var payload = AlphaEnvelope();
        await using var input = new MemoryStream(SuccessVector(Request, payload));
        var result = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
        Assert.Equal(3, result.Variants.Count);
        Assert.All(result.Variants, variant => Assert.Equal(payload, variant.Content));
    }

    [Theory]
    [InlineData(20, 0)] // ALPH without the advertised alpha flag.
    [InlineData(20, 18)] // Animation cannot accompany a static candidate.
    [InlineData(21, 1)] // Reserved VP8X bits.
    [InlineData(24, 40)] // Canvas width differs from the frame.
    [InlineData(34, 0)] // Missing alpha payload.
    [InlineData(38, 192)] // Reserved ALPH bits.
    [InlineData(38, 2)] // Unknown alpha compression.
    public async Task ExtendedAlphaEnvelopeRejectsInconsistentOrUnknownFields(int offset, byte value)
    {
        var payload = AlphaEnvelope();
        payload[offset] = value;
        await using var input = new MemoryStream(SuccessVector(Request, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
    }

    private static byte[] AlphaEnvelope()
    {
        byte[] payload = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 10, 0, 0, 0,
            16, 0, 0, 0, 39, 0, 0, 19, 0, 0, .. "ALPH"u8, 2, 0, 0, 0, 0, 64, .. WebpHeader()[12..]];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)(payload.Length - 8));
        return payload;
    }

    [Fact]
    public async Task DuplicateFrameKindsCannotSatisfyTheThreeFrameRequirement()
    {
        var bytes = SuccessVector(Request);
        bytes[141] = 1;
        await using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(23)]
    [InlineData(26)]
    public async Task ImageFramingAndDimensionsMustAgreeEvenWhenPayloadHashMatches(int offset)
    {
        var payload = WebpHeader();
        payload[offset]++;
        await using var input = new MemoryStream(SuccessVector(Request, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
    }

    [Theory]
    [InlineData("EXIF")]
    [InlineData("XMP ")]
    [InlineData("ICCP")]
    [InlineData("ANIM")]
    [InlineData("ZZZZ")]
    public async Task AValidTransportHashDoesNotAuthorizeMetadataOrUnknownImageChunks(string chunk)
    {
        var payload = WebpHeader().Concat(System.Text.Encoding.ASCII.GetBytes(chunk)).Concat(new byte[4]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)(payload.Length - 8));
        await using var input = new MemoryStream(SuccessVector(Request, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
    }

    [Fact]
    public async Task AnEmptyRiffContainerCannotMasqueradeAsASanitizedImage()
    {
        byte[] payload = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8];
        await using var input = new MemoryStream(SuccessVector(Request, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.ReadResponseAsync(input, Request, Ct));
    }

    private static byte[] SuccessVector(SanitizerRequest request, byte[]? content = null)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("DMRS"u8);
        writer.Write((byte)1);
        writer.Write(request.Nonce);
        writer.Write(SHA256.HashData(request.Content));
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write(40);
        writer.Write(20);
        writer.Write((byte)3);
        // A framing-only payload; image decoding is exercised separately against the full synthetic corpus.
        var payload = content ?? WebpHeader();
        for (byte kind = 1; kind <= 3; kind++)
        {
            writer.Write(kind);
            writer.Write(40);
            writer.Write(20);
            writer.Write(payload.Length);
            writer.Write(SHA256.HashData(payload));
            writer.Write(payload);
        }
        return output.ToArray();
    }

    private static byte[] RequestVector(SanitizerRequest request)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("DMRQ"u8);
        writer.Write((byte)1);
        writer.Write(request.Nonce);
        writer.Write((byte)1);
        writer.Write(request.Content.Length);
        writer.Write(SHA256.HashData(request.Content));
        writer.Write(request.Budget.VariantBytes);
        writer.Write(request.Budget.TotalBytes);
        writer.Write(request.Budget.StderrBytes);
        writer.Write(request.Budget.DeadlineMilliseconds);
        writer.Write(request.Content);
        return output.ToArray();
    }

    private static byte[] RejectionVector(SanitizerRequest request, byte reason) =>
        [.. "DMRS"u8, 1, .. request.Nonce, .. SHA256.HashData(request.Content), 0, reason];

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class HeaderOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.True(Position < Length, "Invalid lengths must be rejected before attempting payload reads.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class OpenTailStream(byte[] bytes, CancellationTokenSource timeout) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            await timeout.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
