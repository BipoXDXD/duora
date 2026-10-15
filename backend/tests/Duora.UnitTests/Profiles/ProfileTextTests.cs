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

/// <summary>
/// Classes de caractere que deixam um nome parecer outro ou esconder conteúdo (revisão de segurança do PR): cada
/// classe tem o seu caso, no nome e na descrição.
/// </summary>
public sealed class ProfileTextCharacterClassTests
{
    public static TheoryData<string, string> HiddenOrFormattingCharacters { get; } = new()
    {
        { "NUL (Cc)", "\u0000" },
        { "BEL (Cc)", "\u0007" },
        { "DEL (Cc)", "\u007F" },
        { "C1 CSI (Cc)", "\u009B" },
        { "LRM (Cf, bidi)", "\u200E" },
        { "RLM (Cf, bidi)", "\u200F" },
        { "LRE (Cf, bidi)", "\u202A" },
        { "RLO (Cf, bidi)", "\u202E" },
        { "LRI (Cf, bidi)", "\u2066" },
        { "PDI (Cf, bidi)", "\u2069" },
        { "ZWSP (Cf, largura zero)", "\u200B" },
        { "ZWNJ (Cf, largura zero)", "\u200C" },
        { "ZWJ (Cf, largura zero)", "\u200D" },
        { "WORD JOINER (Cf, largura zero)", "\u2060" },
        { "BOM/ZWNBSP (Cf, largura zero)", "\uFEFF" },
        { "SOFT HYPHEN (Cf)", "\u00AD" },
        { "LINE SEPARATOR (Zl)", "\u2028" },
        { "PARAGRAPH SEPARATOR (Zp)", "\u2029" },
        { "surrogate alto solto (Cs)", "\uD800" },
        { "surrogate baixo solto (Cs)", "\uDC00" },
    };

    [Theory]
    [MemberData(nameof(HiddenOrFormattingCharacters))]
    public void DisplayNameRejectsTheCharacterInsideTheName(string characterClass, string character)
    {
        Assert.False(DisplayName.TryParse("Ana" + character + "Souza", out _, out var problem), characterClass);
        Assert.Equal(ProfileTextProblem.ForbiddenCharacter, problem);
    }

    [Theory]
    [MemberData(nameof(HiddenOrFormattingCharacters))]
    public void BioRejectsTheCharacterInsideTheText(string characterClass, string character)
    {
        Assert.False(ProfileBio.TryParse("Oi" + character + "tudo bem", out _, out var problem), characterClass);
        Assert.Equal(ProfileTextProblem.ForbiddenCharacter, problem);
    }

    /// <summary>Um nome feito só de caractere invisível não pode passar como nome.</summary>
    [Theory]
    [MemberData(nameof(HiddenOrFormattingCharacters))]
    public void DisplayNameMadeOnlyOfTheCharacterIsRejected(string characterClass, string character)
    {
        Assert.False(DisplayName.TryParse(character, out _, out _), characterClass);
    }

    public static TheoryData<string> SeparatorsOnly { get; } =
    [
        " ",
        "\u00A0",
        "\u2003",
        "\u3000",
        " \u00A0\u2003 ",
    ];

    [Theory]
    [MemberData(nameof(SeparatorsOnly))]
    public void DisplayNameOfSeparatorsOnlyIsEmpty(string value)
    {
        Assert.False(DisplayName.TryParse(value, out _, out var problem));
        Assert.Equal(ProfileTextProblem.Empty, problem);
    }

    [Fact]
    public void DisplayNameTrimsUnicodeSeparatorsAtTheEdges()
    {
        Assert.True(DisplayName.TryParse("\u00A0\u3000Ana\u2003", out var name, out _));
        Assert.Equal("Ana", name!.Value);
    }

    /// <summary>
    /// "é" decomposto (e + U+0301) e composto são o mesmo nome na tela: guardado em NFC, os dois viram o mesmo texto,
    /// e o tamanho é medido depois de normalizar.
    /// </summary>
    [Fact]
    public void DisplayNameIsStoredInNfc()
    {
        Assert.True(DisplayName.TryParse("Jose\u0301", out var name, out _));
        Assert.Equal("Jos\u00E9", name!.Value);
    }

    [Fact]
    public void LengthIsMeasuredAfterNfc()
    {
        // 40 "é" decompostos: 80 runes antes da NFC, 40 depois.
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", DisplayName.MaxLength));

        Assert.True(DisplayName.TryParse(decomposed, out var name, out _));
        Assert.Equal(DisplayName.MaxLength, name!.Value.Length);
    }

    [Fact]
    public void BioIsStoredInNfc()
    {
        Assert.True(ProfileBio.TryParse("cafe\u0301", out var bio, out _));
        Assert.Equal("caf\u00E9", bio!.Value);
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
