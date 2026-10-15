using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

public sealed class ReportNarrativeTests
{
    private const string Emoji = "\U0001F600";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ReportNarrative.MaxLength)]
    public void AcceptsFromEmptyUpToTheMaximum(int length)
    {
        var text = new string('a', length);

        Assert.Equal(text, ReportNarrative.Parse(text).Text);
    }

    [Fact]
    public void RejectsOneCharacterAboveTheMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportNarrative.Parse(new string('a', ReportNarrative.MaxLength + 1)));
    }

    [Fact]
    public void CountsUnicodeCharactersNotUtf16Units()
    {
        var atLimit = string.Concat(Enumerable.Repeat(Emoji, ReportNarrative.MaxLength));

        Assert.Equal(atLimit, ReportNarrative.Parse(atLimit).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportNarrative.Parse(atLimit + Emoji));
    }

    [Fact]
    public void MissingNarrativeIsEmpty()
    {
        Assert.Equal(ReportNarrative.Empty, ReportNarrative.Parse(null));
    }

    [Theory]
    [InlineData("antes\u0000depois")]
    [InlineData("sino\u0007")]
    [InlineData("escape\u001b[31m")]
    public void RejectsControlCharacters(string text)
    {
        Assert.Throws<ArgumentException>(() => ReportNarrative.Parse(text));
    }

    [Fact]
    public void RejectsALoneSurrogate()
    {
        // Montado em código: o xUnit serializa dados de [InlineData] e trocaria o surrogate por U+FFFD.
        var broken = "metade de par " + Emoji[0];

        Assert.Throws<ArgumentException>(() => ReportNarrative.Parse(broken));
    }

    [Fact]
    public void KeepsLineBreaksAndTabs()
    {
        const string MultiLine = "linha 1\r\nlinha 2\tfim";

        Assert.Equal(MultiLine, ReportNarrative.Parse(MultiLine).Text);
    }
}
