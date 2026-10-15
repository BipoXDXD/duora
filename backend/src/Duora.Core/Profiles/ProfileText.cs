using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Duora.Core.Profiles;

/// <summary>Por que um texto do perfil foi recusado; a API traduz cada um num código estável por campo.</summary>
public enum ProfileTextProblem
{
    Empty,
    TooLong,
    ForbiddenCharacter,
}

/// <summary>
/// Nome de exibição (spec 1.1; <c>profiles.profiles.display_name</c>, até 40 caracteres em docs/data/model.md). Uma
/// linha só: sem quebra, sem caractere de controle e sem controle bidirecional, que deixaria um nome se passar por
/// outro na tela de quem o vê.
/// </summary>
public sealed record DisplayName
{
    public const int MaxLength = 40;

    private DisplayName(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(
        string value,
        [NotNullWhen(true)] out DisplayName? name,
        out ProfileTextProblem problem)
    {
        name = ProfileText.TryNormalize(value, MaxLength, allowLineBreaks: false, out var normalized, out problem)
            ? new DisplayName(normalized)
            : null;
        return name is not null;
    }

    public override string ToString() => Value;
}

/// <summary>
/// Descrição curta (spec 1.1; <c>profiles.profiles.bio</c>, até 500 caracteres em docs/data/model.md). Texto simples
/// com quebras de linha e tabulação; o resto dos controles é recusado. Apagar a descrição é mandar <c>null</c>, não
/// texto vazio.
/// </summary>
public sealed record ProfileBio
{
    public const int MaxLength = 500;

    private ProfileBio(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(
        string value,
        [NotNullWhen(true)] out ProfileBio? bio,
        out ProfileTextProblem problem)
    {
        bio = ProfileText.TryNormalize(value, MaxLength, allowLineBreaks: true, out var normalized, out problem)
            ? new ProfileBio(normalized)
            : null;
        return bio is not null;
    }

    public override string ToString() => Value;
}

/// <summary>
/// Regras comuns aos textos livres do perfil. O tamanho conta caracteres Unicode (runes), como a spec 4.3 faz para
/// mensagens e como o <c>char_length</c> do PostgreSQL conta, para frontend, API e CHECK do banco concordarem.
/// </summary>
internal static class ProfileText
{
    public static bool TryNormalize(
        string value,
        int maxLength,
        bool allowLineBreaks,
        out string normalized,
        out ProfileTextProblem problem)
    {
        ArgumentNullException.ThrowIfNull(value);
        normalized = value.Trim();
        var found = FindProblem(normalized, maxLength, allowLineBreaks);
        problem = found.GetValueOrDefault();
        return found is null;
    }

    private static ProfileTextProblem? FindProblem(string trimmed, int maxLength, bool allowLineBreaks)
    {
        if (trimmed.Length == 0)
        {
            return ProfileTextProblem.Empty;
        }

        var length = 0;
        var remaining = trimmed.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done
                || IsForbidden(rune, allowLineBreaks))
            {
                return ProfileTextProblem.ForbiddenCharacter;
            }

            length++;
            remaining = remaining[consumed..];
        }

        return length > maxLength ? ProfileTextProblem.TooLong : null;
    }

    private static bool IsForbidden(Rune rune, bool allowLineBreaks)
    {
        if (allowLineBreaks && rune.Value is '\n' or '\r' or '\t')
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
            || IsBidiControl(rune.Value);
    }

    /// <summary>Embeddings, overrides e isolates (U+202A–U+202E, U+2066–U+2069): invertem a ordem exibida.</summary>
    private static bool IsBidiControl(int codePoint) =>
        codePoint is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
}
