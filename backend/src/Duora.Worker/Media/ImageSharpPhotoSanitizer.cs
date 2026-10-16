using Duora.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Duora.Worker.Media;

/// <summary>Limites da sanitização (seção <c>Media:Processing</c>, ADR-0025).</summary>
internal sealed class PhotoSanitizerOptions
{
    /// <summary>Teto de memória de todas as alocações do decoder. 20 MP em RGBA são 80 MB, mais os derivados.</summary>
    public int MemoryLimitMegabytes { get; set; } = 256;

    /// <summary>
    /// Prazo cooperativo: o token é conferido entre as etapas (identificação, decode, cada reencode), e um decode já
    /// iniciado não é interrompido. O tempo de um decode é limitado pelo teto de 20 MP e pelo paralelismo 1.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public int WebPQuality { get; set; } = 80;

    public bool IsConsistent() => MemoryLimitMegabytes >= 1 && Timeout > TimeSpan.Zero && WebPQuality is >= 1 and <= 100;
}

/// <summary>
/// Sanitizador de fotos com ImageSharp (ADR-0025), na ordem do pipeline: assinatura, animação pela estrutura, header
/// (dimensões e frames antes de alocar pixels), decode limitado, orientação, sRGB, descarte de metadados e reencode em
/// WebP. A <see cref="Configuration"/> só conhece JPEG, PNG e WebP: nenhum outro decoder do pacote é alcançável.
/// </summary>
internal sealed class ImageSharpPhotoSanitizer : IPhotoSanitizer
{
    private readonly PhotoSanitizerOptions _options;
    private readonly Configuration _configuration;
    private readonly WebpEncoder _encoder;

    public ImageSharpPhotoSanitizer(PhotoSanitizerOptions options)
    {
        _options = options;
        _configuration = new Configuration(
            new JpegConfigurationModule(),
            new PngConfigurationModule(),
            new WebpConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                AccumulativeAllocationLimitMegabytes = options.MemoryLimitMegabytes,
                AllocationLimitMegabytes = options.MemoryLimitMegabytes,
                SingleBufferAllocationLimitMegabytes = options.MemoryLimitMegabytes,
            }),
            // Concorrência 1 também dentro de uma imagem: o Worker não disputa CPU com o resto do host.
            MaxDegreeOfParallelism = 1,
        };
        _encoder = new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = options.WebPQuality, SkipMetadata = true };
    }

    public async Task<SanitizeOutcome> SanitizeAsync(
        PhotoFormat declared,
        ReadOnlyMemory<byte> original,
        CancellationToken cancellationToken)
    {
        // A recepção e o CHECK de size_bytes já limitam o original; o decoder não depende deles para não ver mais.
        if (original.Length > PhotoLimits.MaxBytes)
        {
            return new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit);
        }

        var detected = PhotoFormats.Sniff(original.Span);
        if (PhotoInspection.CheckSignature(declared, detected) is { } signature)
        {
            return new SanitizeOutcome.Rejected(signature);
        }

        var format = detected!.Value;
        if (PhotoAnimation.IsAnimated(format, original.Span))
        {
            return new SanitizeOutcome.Rejected(PhotoRejection.Animated);
        }

        if (!PhotoCompleteness.IsComplete(format, original.Span))
        {
            return new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            return await DecodeAsync(format, original, timeout.Token);
        }
        catch (Exception exception) when (IsMemoryLimit(exception))
        {
            // O decoder embrulha a recusa do alocador num InvalidImageContentException.
            return new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit);
        }
        catch (Exception exception) when (exception is ImageFormatException or UnknownImageFormatException
            or InvalidImageContentException or NotSupportedException or InvalidDataException)
        {
            // InvalidDataException: o inflate do .NET lança direto, sem embrulhar, num IDAT de PNG que não é deflate.
            return new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage);
        }
    }

    private static bool IsMemoryLimit(Exception exception) =>
        exception is InvalidMemoryOperationException || exception.InnerException is InvalidMemoryOperationException;

    private async Task<SanitizeOutcome> DecodeAsync(PhotoFormat format, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)
    {
        var decoder = new DecoderOptions
        {
            Configuration = _configuration,
            MaxFrames = 1,
            ColorProfileHandling = ColorProfileHandling.Convert,
            SegmentIntegrityHandling = SegmentIntegrityHandling.Strict,
        };

        await using var header = new MemoryStream(original.ToArray(), writable: false);
        var info = await Image.IdentifyAsync(decoder, header, cancellationToken);
        if (PhotoInspection.CheckHeader(info.Width, info.Height, info.FrameCount) is { } rejection)
        {
            return new SanitizeOutcome.Rejected(rejection);
        }

        header.Position = 0;
        using var image = await Image.LoadAsync<Rgba32>(decoder, header, cancellationToken);
        image.Mutate(operation => operation.AutoOrient());
        StripMetadata(image);

        var variants = new List<SanitizedVariant>(PhotoVariants.All.Count);
        foreach (var kind in PhotoVariants.All)
        {
            variants.Add(await EncodeAsync(image, kind, cancellationToken));
        }

        return new SanitizeOutcome.Sanitized(format, image.Width, image.Height, variants);
    }

    /// <summary>EXIF (com GPS), XMP, IPTC e ICC saem da imagem e de cada frame; o ICC já foi convertido para sRGB.</summary>
    private static void StripMetadata(Image<Rgba32> image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.CicpProfile = null;
        foreach (var frame in image.Frames)
        {
            frame.Metadata.ExifProfile = null;
            frame.Metadata.XmpProfile = null;
            frame.Metadata.IccProfile = null;
            frame.Metadata.CicpProfile = null;
        }
    }

    private async Task<SanitizedVariant> EncodeAsync(Image<Rgba32> image, PhotoVariant kind, CancellationToken cancellationToken)
    {
        var (width, height) = PhotoVariants.Fit(image.Width, image.Height, PhotoVariants.MaxSide(kind));
        using var resized = image.Clone(operation =>
        {
            if (width != image.Width || height != image.Height)
            {
                operation.Resize(width, height);
            }
        });
        await using var output = new MemoryStream();
        await resized.SaveAsync(output, _encoder, cancellationToken);
        return new SanitizedVariant(kind, output.ToArray(), width, height);
    }
}
