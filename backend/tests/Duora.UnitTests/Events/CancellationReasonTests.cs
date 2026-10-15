using Duora.Core.Events;

namespace Duora.UnitTests.Events;

public sealed class CancellationReasonTests
{
    [Fact]
    public void KeepsTheTrimmedText()
    {
        var reason = new CancellationReason("  Local indisponível  ");

        Assert.Equal("Local indisponível", reason.Text);
    }

    [Fact]
    public void AcceptsTheMaximumLength()
    {
        var text = new string('a', CancellationReason.MaxLength);

        Assert.Equal(text, new CancellationReason(text).Text);
    }

    [Fact]
    public void RejectsTextAboveTheMaximumLength()
    {
        Assert.Throws<ArgumentException>(() => new CancellationReason(new string('a', CancellationReason.MaxLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("motivo\0oculto")]
    [InlineData("linha\u0007sino")]
    public void RejectsBlankOrControlCharacters(string text)
    {
        Assert.Throws<ArgumentException>(() => new CancellationReason(text));
    }

    [Fact]
    public void AcceptsLineBreaksInsideTheText()
    {
        var reason = new CancellationReason("Chuva forte.\nRemarcaremos.");

        Assert.Equal("Chuva forte.\nRemarcaremos.", reason.Text);
    }
}
