using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

public sealed class EmailLanguageTagTests
{
    [Theory]
    [InlineData("pt-BR", EmailLanguage.BrazilianPortuguese)]
    [InlineData("pt", EmailLanguage.BrazilianPortuguese)]
    [InlineData("PT-br", EmailLanguage.BrazilianPortuguese)]
    [InlineData("pt-PT", EmailLanguage.BrazilianPortuguese)]
    [InlineData("en", EmailLanguage.English)]
    [InlineData("en-US", EmailLanguage.English)]
    [InlineData("EN-gb", EmailLanguage.English)]
    public void SupportedTagMatchesByPrimaryLanguage(string tag, EmailLanguage expected)
    {
        Assert.Equal(expected, EmailLanguageTag.Match(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("fr-FR")]
    [InlineData("es")]
    [InlineData("english")]
    [InlineData("ptx")]
    [InlineData("pt\u0000")]
    [InlineData("-en")]
    public void TagOutsideTheAllowlistDoesNotMatch(string? tag)
    {
        Assert.Null(EmailLanguageTag.Match(tag));
    }

    [Theory]
    [InlineData(EmailLanguage.BrazilianPortuguese, "pt-BR")]
    [InlineData(EmailLanguage.English, "en-US")]
    public void FormatWritesTheCanonicalTag(EmailLanguage language, string expected)
    {
        Assert.Equal(expected, EmailLanguageTag.Format(language));
    }

    [Theory]
    [InlineData("en", EmailLanguage.English)]
    [InlineData("pt-BR", EmailLanguage.BrazilianPortuguese)]
    [InlineData(null, EmailLanguage.BrazilianPortuguese)]
    [InlineData("xx", EmailLanguage.BrazilianPortuguese)]
    [InlineData("fr", EmailLanguage.BrazilianPortuguese)]
    [InlineData("pt-PT", EmailLanguage.BrazilianPortuguese)]
    [InlineData("en-US", EmailLanguage.English)]
    public void ParseOrDefaultFallsBackToBrazilianPortuguese(string? tag, EmailLanguage expected)
    {
        Assert.Equal(expected, EmailLanguageTag.ParseOrDefault(tag));
    }
}
