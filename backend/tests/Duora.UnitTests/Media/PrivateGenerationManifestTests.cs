using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PrivateGenerationManifestTests
{
    private const string Hash = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81";

    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(16_777_216, 4_194_304, 4_194_304, true)]
    [InlineData(0, 1, 1, false)]
    [InlineData(16_777_217, 1, 1, false)]
    [InlineData(8_388_609, 8_388_609, 8_388_609, false)]
    public void TheManifestEnforcesBothTheRealVariantAndAggregateEnvelope(
        long thumb, long card, long full, bool allowed)
    {
        var variants = new PrivateGenerationVariant[]
        {
            new(PhotoVariant.Thumb, thumb, Hash),
            new(PhotoVariant.Card, card, Hash),
            new(PhotoVariant.Full, full, Hash),
        };
        if (allowed)
        {
            PrivateGenerationManifest.Create(new(Guid.NewGuid(), 1, 1), PrivateGenerationArea.Candidate,
                Guid.NewGuid(), variants).Validate();
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => PrivateGenerationManifest.Create(
                new(Guid.NewGuid(), 1, 1), PrivateGenerationArea.Candidate, Guid.NewGuid(), variants));
        }
    }

    [Fact]
    public void ASerializedManifestCannotChangeItsVariantAfterAdmission()
    {
        var manifest = Create();
        manifest.Variants[0] = manifest.Variants[0] with { Sha256 = new string('f', 64) };

        Assert.Throws<InvalidDataException>(manifest.Validate);
    }

    [Fact]
    public void AnIdentityCannotBeMovedToAnotherEpochOrAreaWithoutChangingItsManifest()
    {
        var manifest = Create();
        var moved = manifest with
        {
            Identity = manifest.Identity with { Domain = manifest.Identity.Domain with { Epoch = 2 } },
        };
        var promotion = manifest with { Identity = manifest.Identity with { Area = PrivateGenerationArea.Promotion } };

        Assert.Throws<InvalidDataException>(moved.Validate);
        Assert.Throws<InvalidDataException>(promotion.Validate);
    }

    [Fact]
    public void DuplicateVariantsCannotHideAnUnwrittenFinalVariant()
    {
        var manifest = Create();
        manifest.Variants[2] = manifest.Variants[0];

        Assert.Throws<InvalidDataException>(manifest.Validate);
    }

    [Fact]
    public void PrintingTheContextNeverExposesGenerationOrDomainIdentifiers()
    {
        var manifest = Create();
        var text = string.Join(" ", manifest, manifest.Identity, manifest.Identity.Domain);

        Assert.DoesNotContain(manifest.Identity.Generation.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest.Identity.Generation.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest.Identity.Domain.Id.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
    }

    private static PrivateGenerationManifest Create() => PrivateGenerationManifest.Create(new(Guid.NewGuid(), 1, 1),
        PrivateGenerationArea.Candidate, Guid.NewGuid(),
        PhotoVariants.All.Select(kind => new PrivateGenerationVariant(kind, 3, Hash)));
}
