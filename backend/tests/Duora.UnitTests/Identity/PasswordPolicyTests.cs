using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("abcdefghijkl")]
    [InlineData("correct horse battery staple")]
    [InlineData("só minúsculas e espaço")]
    [InlineData("000000000000")]
    public void AcceptsAnyCompositionFromTwelveCharacters(string password)
    {
        Assert.Null(PasswordPolicy.FindProblem(password));
    }

    [Theory]
    [InlineData(11, PasswordProblem.TooShort)]
    [InlineData(12, null)]
    [InlineData(128, null)]
    [InlineData(129, PasswordProblem.TooLong)]
    public void LengthBoundariesAreTwelveAndOneHundredTwentyEight(int length, PasswordProblem? expected)
    {
        Assert.Equal(expected, PasswordPolicy.FindProblem(new string('x', length)));
    }

    [Fact]
    public void LengthCountsCharactersNotUtf16CodeUnits()
    {
        // 12 emojis ocupam 24 unidades UTF-16, mas são 12 caracteres para quem digita.
        var twelveEmojis = string.Concat(Enumerable.Repeat("\U0001F600", 12));
        var elevenEmojis = string.Concat(Enumerable.Repeat("\U0001F600", 11));

        Assert.Null(PasswordPolicy.FindProblem(twelveEmojis));
        Assert.Equal(PasswordProblem.TooShort, PasswordPolicy.FindProblem(elevenEmojis));
    }

    [Theory]
    [InlineData("abcdefghijkl\u0000")]
    [InlineData("abcdef\nghijkl")]
    [InlineData("\u001babcdefghijkl")]
    public void RejectsControlCharacters(string password)
    {
        Assert.Equal(PasswordProblem.ControlCharacter, PasswordPolicy.FindProblem(password));
    }

    [Fact]
    public void RejectsEmptyPasswordAsTooShort()
    {
        Assert.Equal(PasswordProblem.TooShort, PasswordPolicy.FindProblem(""));
    }
}
