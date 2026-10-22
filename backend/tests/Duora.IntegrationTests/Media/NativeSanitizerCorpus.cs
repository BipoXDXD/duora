using System.Security.Cryptography;
using System.Text.Json;
using Duora.Core.Media;
using SixLabors.ImageSharp;

namespace Duora.IntegrationTests.Media;

internal static class NativeSanitizerCorpus
{
    internal static void AssertEquivalent(SanitizerBaselineCase item, JsonElement baseline, SanitizeOutcome actual)
    {
        Assert.Equal(item.RejectionCode, baseline.GetProperty("RejectionCode").GetString());
        if (item.RejectionCode is not null)
        {
            Assert.Equal("Rejected", baseline.GetProperty("Outcome").GetString());
            Assert.Empty(baseline.GetProperty("Variants").EnumerateArray());
            Assert.Equal(item.RejectionCode, PhotoRejections.Code(Assert.IsType<SanitizeOutcome.Rejected>(actual).Reason));
            return;
        }
        Assert.Equal("Sanitized", baseline.GetProperty("Outcome").GetString());
        var result = Assert.IsType<SanitizeOutcome.Sanitized>(actual);
        Assert.Equal(item.Format, result.Detected);
        Assert.Equal(item.Width, result.Width);
        Assert.Equal(item.Height, result.Height);
        Assert.Equal(baseline.GetProperty("Width").GetInt32(), result.Width);
        Assert.Equal(baseline.GetProperty("Height").GetInt32(), result.Height);
        Assert.Equal(PhotoVariants.All, result.Variants.Select(v => v.Kind));
        var references = baseline.GetProperty("Variants").EnumerateArray().ToArray();
        Assert.Equal(3, references.Length);
        foreach (var (reference, variant) in references.Zip(result.Variants))
        {
            Assert.Equal(reference.GetProperty("Kind").GetString(), PhotoVariants.Name(variant.Kind));
            Assert.Equal(reference.GetProperty("Width").GetInt32(), variant.Width);
            Assert.Equal(reference.GetProperty("Height").GetInt32(), variant.Height);
            Assert.Equal(reference.GetProperty("Bytes").GetInt32(), variant.Content.Length);
            Assert.Equal(reference.GetProperty("Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(variant.Content)));
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(variant.Content));
            var info = Image.Identify(variant.Content);
            Assert.Equal(variant.Width, info.Width);
            Assert.Equal(variant.Height, info.Height);
            Assert.Null(info.Metadata.ExifProfile);
            Assert.Null(info.Metadata.XmpProfile);
            Assert.Null(info.Metadata.IptcProfile);
            Assert.Null(info.Metadata.IccProfile);
            Assert.Null(info.Metadata.CicpProfile);
        }
    }
}
