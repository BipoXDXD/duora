using Duora.Core.Events;

namespace Duora.UnitTests.Events;

public sealed class CancellationReasonTests
{
    [Fact]
    public void ParseKeepsTheTrimmedText()
    {
        var parsed = CancellationReason.Parse("  Local indisponível  ");

        Assert.True(parsed.IsValid);
        Assert.Equal("Local indisponível", parsed.Reason.Text);
        Assert.Null(parsed.Refusal);
    }

    [Fact]
    public void ParseAcceptsTheMaximumLength()
    {
        var text = new string('a', CancellationReason.MaxLength);

        var parsed = CancellationReason.Parse(text);

        Assert.True(parsed.IsValid);
        Assert.Equal(text, parsed.Reason.Text);
    }

    [Fact]
    public void ParseMeasuresTheLengthAfterTrimming()
    {
        var parsed = CancellationReason.Parse($" {new string('a', CancellationReason.MaxLength)} ");

        Assert.True(parsed.IsValid);
    }

    [Fact]
    public void ParseRefusesTextAboveTheMaximumLength()
    {
        AssertRefused(CancellationReasonRefusal.TooLong, new string('a', CancellationReason.MaxLength + 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void ParseRefusesMissingOrBlankText(string? text)
    {
        AssertRefused(CancellationReasonRefusal.Blank, text);
    }

    [Theory]
    [InlineData("motivo\0oculto")]
    [InlineData("linha\u0007sino")]
    [InlineData("tab\tseparado")]
    public void ParseRefusesControlCharacters(string text)
    {
        AssertRefused(CancellationReasonRefusal.ControlCharacter, text);
    }

    [Fact]
    public void ParseAcceptsLineBreaksInsideTheText()
    {
        var parsed = CancellationReason.Parse("Chuva forte.\nRemarcaremos.");

        Assert.True(parsed.IsValid);
        Assert.Equal("Chuva forte.\nRemarcaremos.", parsed.Reason.Text);
    }

    private static void AssertRefused(CancellationReasonRefusal expected, string? text)
    {
        var parsed = CancellationReason.Parse(text);

        Assert.False(parsed.IsValid);
        Assert.Equal(expected, parsed.Refusal);
        Assert.Null(parsed.Reason);
    }
}
