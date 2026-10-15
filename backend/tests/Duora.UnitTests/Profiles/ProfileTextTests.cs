using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("Ana")]
    [InlineData("A")]
    [InlineData("Ana Souza")]
    [InlineData("José 🙂")]
    [InlineData("'; drop table profiles.profiles; --")]
    public void AcceptsPlainNames(string value)
    {
        Assert.True(DisplayName.TryParse(value, out var name, out _));
        Assert.Equal(value, name!.Value);
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        Assert.True(DisplayName.TryParse("  Ana  ", out var name, out _));
        Assert.Equal("Ana", name!.Value);
    }

    [Fact]
    public void AcceptsExactlyTheMaximumCountedInUnicodeCharacters()
    {
        // 40 emojis fora do BMP: 80 unidades UTF-16, 40 caracteres (spec 4.3 conta caracteres Unicode).
        var value = string.Concat(Enumerable.Repeat("😀", DisplayName.MaxLength));

        Assert.True(DisplayName.TryParse(value, out _, out _));
    }

    [Fact]
    public void RejectsOneCharacterAboveTheMaximum()
    {
        var value = new string('a', DisplayName.MaxLength + 1);

        Assert.False(DisplayName.TryParse(value, out var name, out var problem));
        Assert.Null(name);
        Assert.Equal(ProfileTextProblem.TooLong, problem);
    }

    [Fact]
    public void LengthIsCheckedAfterTrimming()
    {
        var value = " " + new string('a', DisplayName.MaxLength) + " ";

        Assert.True(DisplayName.TryParse(value, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u3000")]
    public void RejectsEmptyOrWhitespaceOnly(string value)
    {
        Assert.False(DisplayName.TryParse(value, out _, out var problem));
        Assert.Equal(ProfileTextProblem.Empty, problem);
    }

    /// <summary>
    /// Em código, não em atributo: o metadado de atributo guarda texto em UTF-8 e troca surrogate solto por U+FFFD.
    /// </summary>
    public static TheoryData<string> ForbiddenNames { get; } =
    [
        "Ana\u0000",
        "A\u0007na",
        "Ana\nSouza",
        "Ana\tSouza",
        "Ana\u2028Souza",
        "Ana\u202Eazuos",
        "Ana\u2066x\u2069",
        "Ana\u0085Souza",
        "Ana\uD800",
        "\uDC00Ana",
    ];

    [Theory]
    [MemberData(nameof(ForbiddenNames))]
    public void RejectsControlBidiAndBrokenCharacters(string value)
    {
        Assert.False(DisplayName.TryParse(value, out _, out var problem));
        Assert.Equal(ProfileTextProblem.ForbiddenCharacter, problem);
    }
}

public sealed class ProfileBioTests
{
    [Theory]
    [InlineData("Gosto de jogos de tabuleiro.")]
    [InlineData("Linha um\nLinha dois")]
    [InlineData("Linha um\r\nLinha dois")]
    [InlineData("Coluna\tcoluna")]
    public void AcceptsPlainTextWithLineBreaks(string value)
    {
        Assert.True(ProfileBio.TryParse(value, out var bio, out _));
        Assert.Equal(value, bio!.Value);
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        Assert.True(ProfileBio.TryParse("\n  Oi  \n", out var bio, out _));
        Assert.Equal("Oi", bio!.Value);
    }

    [Fact]
    public void AcceptsExactlyTheMaximum()
    {
        Assert.True(ProfileBio.TryParse(new string('a', ProfileBio.MaxLength), out _, out _));
    }

    [Fact]
    public void RejectsOneCharacterAboveTheMaximum()
    {
        Assert.False(ProfileBio.TryParse(new string('a', ProfileBio.MaxLength + 1), out _, out var problem));
        Assert.Equal(ProfileTextProblem.TooLong, problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public void RejectsWhitespaceOnly(string value)
    {
        Assert.False(ProfileBio.TryParse(value, out _, out var problem));
        Assert.Equal(ProfileTextProblem.Empty, problem);
    }

    public static TheoryData<string> ForbiddenBios { get; } =
    [
        "Oi\u0000",
        "Oi\u001B[31m",
        "Oi\u202E",
        "Oi\uD83D",
    ];

    [Theory]
    [MemberData(nameof(ForbiddenBios))]
    public void RejectsControlBidiAndBrokenCharacters(string value)
    {
        Assert.False(ProfileBio.TryParse(value, out _, out var problem));
        Assert.Equal(ProfileTextProblem.ForbiddenCharacter, problem);
    }
}
