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
/// Regras comuns aos textos livres do perfil, em três passos:
/// <list type="number">
/// <item>Antes da NFC, sobre o texto cru: surrogate solto, noncharacter (U+FDD0–U+FDEF e todo U+xxFFFE/U+xxFFFF), não
/// atribuído (Cn) e uso privado (Co). O <c>Normalize</c> lança com alguns deles (U+FFFE), então eles nunca chegam
/// lá.</item>
/// <item>NFC e aparar as bordas (<c>string.Trim</c>, que inclui NEL e não inclui U+FEFF).</item>
/// <item>Sobre o texto normalizado: Cc (fora quebra de linha e tabulação na descrição), toda a Cf (bidi, largura zero,
/// soft hyphen), Zl e Zp; marca combinante (Mn, Me) só depois de uma base e no máximo
/// <see cref="MaxMarksPerBase"/> por base; seletor de variação e CGJ só logo depois da base, um por base; texto
/// cujas runes visíveis são todas fillers ou brancas conta como vazio; tamanho em runes.</item>
/// </list>
/// O tamanho conta caracteres Unicode (runes) depois da NFC, como o <c>char_length</c> do PostgreSQL, para frontend,
/// API e CHECK do banco concordarem.
/// </summary>
/// <remarks>
/// O custo conhecido: emoji composto com ZWJ (família, profissão com tom de pele) não entra. Homóglifos (cirílico "а"
/// no lugar do latino "a", por exemplo) não são tratados aqui: nome de exibição não é único nem identifica a conta,
/// então a falsificação por semelhança fica com a denúncia e a moderação (Safety), e não com uma lista de confusables
/// que recusaria nomes legítimos em outros alfabetos.
/// </remarks>
internal static class ProfileText
{
    /// <summary>Marcas combinantes seguidas sobre uma base; acima disso é texto "zalgo", que invade outras linhas.</summary>
    public const int MaxMarksPerBase = 4;

    private const int CombiningGraphemeJoiner = 0x034F;
    private const int MongolianFreeVariationSelector1 = 0x180B;

    public static bool TryNormalize(
        string value,
        int maxLength,
        bool allowLineBreaks,
        out string normalized,
        out ProfileTextProblem problem)
    {
        ArgumentNullException.ThrowIfNull(value);
        normalized = "";

        if (!IsSafeToNormalize(value))
        {
            problem = ProfileTextProblem.ForbiddenCharacter;
            return false;
        }

        var candidate = value.Normalize(NormalizationForm.FormC).Trim();
        var found = FindProblem(candidate, maxLength, allowLineBreaks);
        problem = found.GetValueOrDefault();
        if (found is not null)
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    private static bool IsSafeToNormalize(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done
                || IsNoncharacter(rune.Value)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    /// <summary>
    /// Defesa em profundidade: todo noncharacter já é Cn no Unicode, mas a categoria vem da tabela do runtime, e é
    /// esta checagem que garante que nenhum chegue ao <c>Normalize</c> (U+FFFE o faz lançar) se a tabela mudar.
    /// </summary>
    private static bool IsNoncharacter(int codePoint) =>
        codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

    private static ProfileTextProblem? FindProblem(string trimmed, int maxLength, bool allowLineBreaks)
    {
        var length = 0;
        var hasVisible = false;
        var cluster = new ClusterState();
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (IsForbidden(rune, allowLineBreaks) || !cluster.Accept(rune))
            {
                return ProfileTextProblem.ForbiddenCharacter;
            }

            hasVisible |= IsVisible(rune);
            length++;
        }

        if (!hasVisible)
        {
            return ProfileTextProblem.Empty;
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
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.PrivateUse;
    }

    /// <summary>Visível: nem branco, nem marca, nem filler que se desenha vazio (Hangul, braille em branco).</summary>
    private static bool IsVisible(Rune rune) =>
        !Rune.IsWhiteSpace(rune)
        && !IsMark(rune)
        && !IsSelector(rune.Value)
        && rune.Value is not (0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800 or '\n' or '\r' or '\t');

    private static bool IsMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Seletores do suplemento (U+E0100–U+E01EF): só existem sequências registradas para ideogramas (IVS), então só
    /// valem depois de um ideograma Han.
    /// </summary>
    private static bool IsIdeographicSelector(int codePoint) => codePoint is >= 0xE0100 and <= 0xE01EF;

    /// <summary>Blocos de ideogramas unificados e de compatibilidade CJK (mesmos intervalos do profile-text.ts).</summary>
    private static bool IsHanIdeograph(int codePoint) =>
        codePoint is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x20000 and <= 0x2FA1F) or (>= 0x30000 and <= 0x323AF);

    /// <summary>Seletores de variação (FE00–FE0F, E0100–E01EF, FVS1) e CGJ: modificam a base anterior.</summary>
    private static bool IsSelector(int codePoint) =>
        codePoint is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
            or MongolianFreeVariationSelector1 or CombiningGraphemeJoiner;

    /// <summary>
    /// Base é qualquer rune que não seja marca, seletor, branco nem quebra de linha. Marca exige uma base antes, e o
    /// seletor exige vir logo depois dela (sem marca no meio), um por base.
    /// </summary>
    private struct ClusterState
    {
        private bool _hasBase;
        private bool _lastWasBase;
        private bool _baseIsHan;
        private bool _selectorUsed;
        private int _marks;

        public bool Accept(Rune rune)
        {
            if (IsSelector(rune.Value))
            {
                if (!_lastWasBase || _selectorUsed || (IsIdeographicSelector(rune.Value) && !_baseIsHan))
                {
                    return false;
                }

                _selectorUsed = true;
                _lastWasBase = false;
                return true;
            }

            if (IsMark(rune))
            {
                _marks++;
                _lastWasBase = false;
                return _hasBase && _marks <= MaxMarksPerBase;
            }

            var isBase = !Rune.IsWhiteSpace(rune);
            _hasBase = isBase;
            _lastWasBase = isBase;
            _baseIsHan = IsHanIdeograph(rune.Value);
            _selectorUsed = false;
            _marks = 0;
            return true;
        }
    }
}
