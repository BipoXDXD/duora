using System.Security.Cryptography;
using System.Text.Json;
using Duora.Core.Media;
using Xunit.Sdk;

namespace Duora.IntegrationTests.Media;

public sealed class NativeSanitizerCorpusTests
{
    private static SanitizerBaselineCase Case => new("synthetic-webp", PhotoFormat.WebP, () => TestImages.WebP());

    private static SanitizeOutcome.Sanitized Sanitized(byte[]? content = null) =>
        new(PhotoFormat.WebP, 64, 32, PhotoVariants.All.Select(kind =>
            new SanitizedVariant(kind, content ?? TestImages.WebP(), 64, 32)).ToArray());

    private static JsonElement Reference(SanitizeOutcome.Sanitized result, string? hash = null) =>
        JsonSerializer.SerializeToElement(new
        {
            Outcome = "Sanitized",
            RejectionCode = (string?)null,
            result.Width,
            result.Height,
            Variants = result.Variants.Select(variant => new
            {
                Kind = PhotoVariants.Name(variant.Kind),
                Bytes = variant.Content.Length,
                Sha256 = hash ?? Convert.ToHexStringLower(SHA256.HashData(variant.Content)),
                variant.Width,
                variant.Height,
                IdentifiedWidth = variant.Width,
                IdentifiedHeight = variant.Height,
                Format = "WebP",
                HasMetadata = false,
            }).ToArray(),
        });

    [Fact]
    public void ExactSanitizedVariantsMatchTheBaseline()
    {
        var actual = Sanitized();
        NativeSanitizerCorpus.AssertEquivalent(Case, Reference(actual), actual);
    }

    [Fact]
    public void ExactTechnicalRejectionMatchesTheBaseline()
    {
        var item = Case with { RejectionCode = "ANIMATED" };
        var baseline = JsonSerializer.SerializeToElement(new
        {
            Outcome = "Rejected",
            RejectionCode = "ANIMATED",
            Variants = Array.Empty<object>(),
        });
        NativeSanitizerCorpus.AssertEquivalent(item, baseline, new SanitizeOutcome.Rejected(PhotoRejection.Animated));
    }

    [Fact]
    public void ARejectionCannotReplaceSanitizedBytes()
    {
        var baseline = Reference(Sanitized());
        Assert.ThrowsAny<XunitException>(() => NativeSanitizerCorpus.AssertEquivalent(
            Case, baseline, new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit)));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("dimensions")]
    [InlineData("missing-variant")]
    [InlineData("order")]
    [InlineData("bytes")]
    public void DifferentOutputsCannotQualify(string change)
    {
        var actual = Sanitized();
        var changed = change switch
        {
            "format" => actual with { Detected = PhotoFormat.Jpeg },
            "dimensions" => actual with { Width = 63 },
            "missing-variant" => actual with { Variants = actual.Variants.Take(2).ToArray() },
            "order" => actual with { Variants = actual.Variants.Reverse().ToArray() },
            "bytes" => actual with { Variants = actual.Variants.Select(v => v with { Content = [.. v.Content, 0] }).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.ThrowsAny<XunitException>(() => NativeSanitizerCorpus.AssertEquivalent(Case, Reference(actual), changed));
    }

    [Fact]
    public void ChangedHashesCannotQualify()
    {
        var actual = Sanitized();
        Assert.ThrowsAny<XunitException>(() => NativeSanitizerCorpus.AssertEquivalent(
            Case, Reference(actual, new string('f', 64)), actual));
    }

    [Fact]
    public void RetainedMetadataCannotQualifyEvenWhenBytesAndDimensionsMatch()
    {
        var actual = Sanitized(TestImages.WithEveryMetadata(PhotoFormat.WebP));
        Assert.ThrowsAny<XunitException>(() => NativeSanitizerCorpus.AssertEquivalent(Case, Reference(actual), actual));
    }
}
