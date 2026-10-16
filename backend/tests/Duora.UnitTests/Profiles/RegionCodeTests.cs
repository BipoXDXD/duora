using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

public sealed class RegionCodeTests
{
    [Theory]
    [InlineData("BR-SP")]
    [InlineData("BR-AC")]
    [InlineData("BR-TO")]
    [InlineData("BR-DF")]
    public void AcceptsBrazilianStateCodes(string value)
    {
        Assert.True(RegionCode.TryParse(value, out var region));
        Assert.Equal(value, region.Value);
    }

    [Fact]
    public void KnowsExactlyTheTwentySevenFederativeUnits()
    {
        Assert.Equal(
            [
                "BR-AC", "BR-AL", "BR-AM", "BR-AP", "BR-BA", "BR-CE", "BR-DF", "BR-ES", "BR-GO", "BR-MA", "BR-MG",
                "BR-MS", "BR-MT", "BR-PA", "BR-PB", "BR-PE", "BR-PI", "BR-PR", "BR-RJ", "BR-RN", "BR-RO", "BR-RR",
                "BR-RS", "BR-SC", "BR-SE", "BR-SP", "BR-TO",
            ],
            RegionCode.All.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A forma canônica é a única aceita: minúscula, borda com espaço ou outro separador não viram BR-SP em silêncio,
    /// para o contrato (enum) e o CHECK do banco dizerem a mesma coisa.
    /// </summary>
    [Theory]
    [InlineData("br-sp")]
    [InlineData("Br-Sp")]
    [InlineData(" BR-SP")]
    [InlineData("BR-SP ")]
    [InlineData("BR_SP")]
    [InlineData("BRSP")]
    [InlineData("SP")]
    [InlineData("BR-XX")]
    [InlineData("BR-")]
    [InlineData("US-CA")]
    [InlineData("")]
    [InlineData("BR-SP\u0000")]
    [InlineData("\u0000")]
    [InlineData("BR‑SP")]
    public void RejectsAnythingElse(string value)
    {
        Assert.False(RegionCode.TryParse(value, out var region));
        Assert.Null(region);
    }

    /// <summary>
    /// Como <c>DisplayName.TryParse</c> e <c>ProfileBio.TryParse</c>, <c>null</c> não é entrada: quem chama trata o
    /// <c>null</c> do JSON (limpar a região) antes do parse.
    /// </summary>
    [Fact]
    public void NullIsAProgrammingErrorNotAnUnknownRegion()
    {
        Assert.Throws<ArgumentNullException>(() => RegionCode.TryParse(null!, out _));
    }
}
