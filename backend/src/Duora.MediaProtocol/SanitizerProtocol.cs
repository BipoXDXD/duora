using System.Buffers.Binary;
using System.Security.Cryptography;
using Duora.Core.Media;

namespace Duora.MediaProtocol;

public sealed record ProtocolBudget(int VariantBytes, int TotalBytes, int StderrBytes, int DeadlineMilliseconds)
{
    public static ProtocolBudget Candidate { get; } = new(16 * 1024 * 1024, 24 * 1024 * 1024, 64 * 1024, 50_000);
}

public sealed record SanitizerRequest(byte[] Nonce, PhotoFormat Format, byte[] Content, ProtocolBudget Budget);

/// <summary>Bounded one-request/one-response wire contract. No decoder, transport or activation.</summary>
public static class SanitizerProtocol
{
    public static async Task<SanitizerRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[74];
        await ReadExactlyAsync(stream, header, cancellationToken);
        Require(header.AsSpan(0, 4).SequenceEqual("DMRQ"u8) && header[4] == 1);
        var format = ReadFormat(header[21]);
        var length = ReadInt(header, 22);
        Require(length > 0 && length <= PhotoLimits.MaxBytes);
        var budget = new ProtocolBudget(ReadInt(header, 58), ReadInt(header, 62), ReadInt(header, 66), ReadInt(header, 70));
        ValidateBudget(budget);
        var content = new byte[length];
        await ReadExactlyAsync(stream, content, cancellationToken);
        Require(CryptographicOperations.FixedTimeEquals(header.AsSpan(26, 32), SHA256.HashData(content)));
        await RequireEndAsync(stream, cancellationToken);
        return new(header[5..21], format, content, budget);
    }

    public static async Task WriteRequestAsync(Stream stream, SanitizerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        var header = new byte[74];
        "DMRQ"u8.CopyTo(header);
        header[4] = 1;
        request.Nonce.CopyTo(header, 5);
        header[21] = WriteFormat(request.Format);
        WriteInt(header, 22, request.Content.Length);
        SHA256.HashData(request.Content).CopyTo(header, 26);
        WriteInt(header, 58, request.Budget.VariantBytes);
        WriteInt(header, 62, request.Budget.TotalBytes);
        WriteInt(header, 66, request.Budget.StderrBytes);
        WriteInt(header, 70, request.Budget.DeadlineMilliseconds);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(request.Content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<SanitizeOutcome> ReadResponseAsync(Stream stream, SanitizerRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var header = new byte[54];
        await ReadExactlyAsync(stream, header, cancellationToken);
        Require(header.AsSpan(0, 4).SequenceEqual("DMRS"u8) && header[4] == 1);
        Require(CryptographicOperations.FixedTimeEquals(header.AsSpan(5, 16), request.Nonce));
        Require(CryptographicOperations.FixedTimeEquals(header.AsSpan(21, 32), SHA256.HashData(request.Content)));
        SanitizeOutcome outcome;
        if (header[53] == 0)
        {
            var code = new byte[1];
            await ReadExactlyAsync(stream, code, cancellationToken);
            outcome = new SanitizeOutcome.Rejected(ReadRejection(code[0]));
        }
        else
        {
            Require(header[53] == 1);
            var metadata = new byte[10];
            await ReadExactlyAsync(stream, metadata, cancellationToken);
            var format = ReadFormat(metadata[0]);
            var width = ReadInt(metadata, 1);
            var height = ReadInt(metadata, 5);
            Require(format == request.Format && metadata[9] == 3);
            ValidateDimensions(width, height);
            var variants = new List<SanitizedVariant>(3);
            long total = 0;
            foreach (var kind in PhotoVariants.All)
            {
                var frame = new byte[45];
                await ReadExactlyAsync(stream, frame, cancellationToken);
                var frameWidth = ReadInt(frame, 1);
                var frameHeight = ReadInt(frame, 5);
                var length = ReadInt(frame, 9);
                Require(frame[0] == WriteKind(kind));
                Require((frameWidth, frameHeight) == PhotoVariants.Fit(width, height, PhotoVariants.MaxSide(kind)));
                Require(length > 0 && length <= request.Budget.VariantBytes);
                total += length;
                Require(total <= request.Budget.TotalBytes);
                var content = new byte[length];
                await ReadExactlyAsync(stream, content, cancellationToken);
                Require(CryptographicOperations.FixedTimeEquals(frame.AsSpan(13, 32), SHA256.HashData(content)));
                Require(PhotoFormats.Sniff(content) == PhotoFormat.WebP);
                variants.Add(new(kind, content, frameWidth, frameHeight));
            }
            outcome = new SanitizeOutcome.Sanitized(format, width, height, variants);
        }
        await RequireEndAsync(stream, cancellationToken);
        return outcome;
    }

    public static async Task WriteResponseAsync(Stream stream, SanitizerRequest request, SanitizeOutcome outcome, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        ValidateOutcome(request, outcome);
        var header = new byte[54];
        "DMRS"u8.CopyTo(header);
        header[4] = 1;
        request.Nonce.CopyTo(header, 5);
        SHA256.HashData(request.Content).CopyTo(header, 21);
        header[53] = outcome is SanitizeOutcome.Sanitized ? (byte)1 : (byte)0;
        await stream.WriteAsync(header, cancellationToken);
        if (outcome is SanitizeOutcome.Rejected rejected)
        {
            await stream.WriteAsync(new byte[] { WriteRejection(rejected.Reason) }, cancellationToken);
        }
        else if (outcome is SanitizeOutcome.Sanitized sanitized)
        {
            var metadata = new byte[10];
            metadata[0] = WriteFormat(sanitized.Detected);
            WriteInt(metadata, 1, sanitized.Width);
            WriteInt(metadata, 5, sanitized.Height);
            metadata[9] = 3;
            await stream.WriteAsync(metadata, cancellationToken);
            foreach (var variant in sanitized.Variants)
            {
                var frame = new byte[45];
                frame[0] = WriteKind(variant.Kind);
                WriteInt(frame, 1, variant.Width);
                WriteInt(frame, 5, variant.Height);
                WriteInt(frame, 9, variant.Content.Length);
                SHA256.HashData(variant.Content).CopyTo(frame, 13);
                await stream.WriteAsync(frame, cancellationToken);
                await stream.WriteAsync(variant.Content, cancellationToken);
            }
        }
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task DrainStderrAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        Require(limit is >= 0 and <= 64 * 1024);
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total + 1)), cancellationToken);
            if (read == 0)
            {
                return;
            }
            total += read;
            Require(total <= limit);
        }
    }

    private static void ValidateRequest(SanitizerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.Nonce is { Length: 16 } && request.Content is { Length: > 0 }
            && request.Content.LongLength <= PhotoLimits.MaxBytes);
        _ = WriteFormat(request.Format);
        ValidateBudget(request.Budget);
    }

    private static void ValidateBudget(ProtocolBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Require(budget.VariantBytes is > 0 and <= 16 * 1024 * 1024
            && budget.TotalBytes is > 0 and <= 24 * 1024 * 1024
            && budget.StderrBytes is >= 0 and <= 64 * 1024
            && budget.DeadlineMilliseconds is > 0 and <= 50_000);
    }

    private static void ValidateOutcome(SanitizerRequest request, SanitizeOutcome outcome)
    {
        if (outcome is SanitizeOutcome.Rejected rejected)
        {
            _ = WriteRejection(rejected.Reason);
            return;
        }
        if (outcome is not SanitizeOutcome.Sanitized sanitized)
        {
            throw new InvalidDataException("Invalid sanitizer protocol message.");
        }
        Require(sanitized.Detected == request.Format && sanitized.Variants.Count == 3);
        ValidateDimensions(sanitized.Width, sanitized.Height);
        long total = 0;
        for (var i = 0; i < 3; i++)
        {
            var variant = sanitized.Variants[i];
            var kind = PhotoVariants.All[i];
            Require(variant.Kind == kind);
            Require((variant.Width, variant.Height) == PhotoVariants.Fit(sanitized.Width, sanitized.Height, PhotoVariants.MaxSide(kind)));
            Require(variant.Content.Length > 0 && variant.Content.Length <= request.Budget.VariantBytes);
            total += variant.Content.Length;
            Require(total <= request.Budget.TotalBytes && PhotoFormats.Sniff(variant.Content) == PhotoFormat.WebP);
        }
    }

    private static void ValidateDimensions(int width, int height) =>
        Require(width > 0 && height > 0 && (long)width * height <= PhotoInspection.MaxPixels);

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Truncated sanitizer protocol message.", exception);
        }
    }

    private static async Task RequireEndAsync(Stream stream, CancellationToken cancellationToken) =>
        Require(await stream.ReadAsync(new byte[1], cancellationToken) == 0);

    private static int ReadInt(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static void WriteInt(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("Invalid sanitizer protocol message.");
        }
    }

    private static PhotoFormat ReadFormat(byte value) => value switch
    {
        1 => PhotoFormat.Jpeg,
        2 => PhotoFormat.Png,
        3 => PhotoFormat.WebP,
        _ => throw new InvalidDataException("Unknown sanitizer format."),
    };

    private static byte WriteFormat(PhotoFormat value) => value switch
    {
        PhotoFormat.Jpeg => 1,
        PhotoFormat.Png => 2,
        PhotoFormat.WebP => 3,
        _ => throw new InvalidDataException("Unknown sanitizer format."),
    };

    private static byte WriteKind(PhotoVariant value) => value switch
    {
        PhotoVariant.Thumb => 1,
        PhotoVariant.Card => 2,
        PhotoVariant.Full => 3,
        _ => throw new InvalidDataException("Unknown sanitizer variant."),
    };

    private static PhotoRejection ReadRejection(byte value) => value switch
    {
        1 => PhotoRejection.UnsupportedFormat,
        2 => PhotoRejection.TypeMismatch,
        3 => PhotoRejection.TooManyPixels,
        4 => PhotoRejection.Animated,
        5 => PhotoRejection.CorruptImage,
        6 => PhotoRejection.ProcessingLimit,
        _ => throw new InvalidDataException("Unknown sanitizer rejection."),
    };

    private static byte WriteRejection(PhotoRejection value) => value switch
    {
        PhotoRejection.UnsupportedFormat => 1,
        PhotoRejection.TypeMismatch => 2,
        PhotoRejection.TooManyPixels => 3,
        PhotoRejection.Animated => 4,
        PhotoRejection.CorruptImage => 5,
        PhotoRejection.ProcessingLimit => 6,
        _ => throw new InvalidDataException("Unknown sanitizer rejection."),
    };
}
