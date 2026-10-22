using System.Buffers.Binary;

namespace Duora.MediaProtocol;

/// <summary>Checks only the metadata-free static lossy envelope produced by the configured encoder; never decodes pixels.</summary>
internal static class WebpEnvelope
{
    private static ReadOnlySpan<byte> Vp8Start => [0x9d, 1, 0x2a];

    internal static void Validate(ReadOnlySpan<byte> bytes, int width, int height)
    {
        Check(bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8));
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == bytes.Length - 8);
        var position = 12;
        var extended = false;
        var alphaFlag = false;
        var alpha = false;
        var image = false;
        while (position < bytes.Length)
        {
            Check(bytes.Length - position >= 8);
            var name = bytes.Slice(position, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
            var end = (long)position + 8 + length + (length & 1);
            Check(end <= bytes.Length);
            var payload = bytes.Slice(position + 8, (int)length);
            Check((length & 1) == 0 || bytes[(int)end - 1] == 0);
            if (name.SequenceEqual("VP8X"u8))
            {
                Check(position == 12 && length == 10);
                Check(payload[0] is 0 or 16 && payload[1] == 0 && payload[2] == 0 && payload[3] == 0);
                Check(Read24(payload[4..]) + 1 == width && Read24(payload[7..]) + 1 == height);
                extended = true;
                alphaFlag = payload[0] == 16;
            }
            else if (name.SequenceEqual("ALPH"u8))
            {
                Check(extended && alphaFlag && !alpha && !image && length > 1);
                Check((payload[0] & 0xc0) == 0 && (payload[0] & 3) <= 1 && ((payload[0] >> 4) & 3) <= 1);
                alpha = true;
            }
            else if (name.SequenceEqual("VP8 "u8))
            {
                Check(!image && (!alphaFlag || alpha) && length >= 10);
                Check((payload[0] & 0x11) == 0x10 && (payload[0] & 0x0e) <= 6);
                Check(payload.Slice(3, 3).SequenceEqual(Vp8Start));
                // The fixed encoder does not request VP8 scaling; compare the full fields, including scale bits.
                Check(BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) == width
                    && BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) == height);
                image = true;
            }
            else
            {
                throw new InvalidDataException("Unexpected sanitizer image chunk.");
            }
            position = (int)end;
        }
        Check(image && alphaFlag == alpha);
    }

    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    private static void Check(bool valid)
    {
        if (!valid)
        {
            throw new InvalidDataException("Invalid sanitizer image envelope.");
        }
    }
}
