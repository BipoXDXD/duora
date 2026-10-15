using System.Buffers;
using System.Text;

namespace Duora.Core.Safety;

/// <summary>
/// Validação dos textos livres do Safety (relato, motivo, recurso). Conta caracteres Unicode (runes), como a
/// spec 4.3 faz para mensagens, para o limite ser o mesmo no frontend e no backend.
/// </summary>
internal static class SafetyText
{
    /// <summary>Texto obrigatório: pelo menos um caractere que não seja espaço.</summary>
    public static string RequireText(string value, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Não pode ser vazio.", paramName);
        }

        return RequireOptionalText(value, maxLength, paramName);
    }

    /// <summary>Texto que pode ser vazio, com os mesmos limites de tamanho e de caracteres.</summary>
    public static string RequireOptionalText(string value, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var length = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Contém UTF-16 inválido.", paramName);
            }

            if (IsForbiddenControl(rune))
            {
                throw new ArgumentException("Contém caractere de controle.", paramName);
            }

            length++;
            if (length > maxLength)
            {
                throw new ArgumentOutOfRangeException(paramName, $"Excede {maxLength} caracteres.");
            }

            remaining = remaining[consumed..];
        }

        return value;
    }

    /// <summary>
    /// Código de catálogo (categoria, motivo da política): começa por letra minúscula e segue com letras minúsculas,
    /// dígitos, `.`, `_` ou `-`, sem espaço nem pontuação que vire injeção em log ou URL.
    /// </summary>
    public static string RequireCatalogCode(string value, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var wellFormed = value.Length > 0
            && value.Length <= maxLength
            && char.IsAsciiLetterLower(value[0])
            && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
        return wellFormed ? value : throw new ArgumentException("Código fora do formato do catálogo.", paramName);
    }

    private static bool IsForbiddenControl(Rune rune) =>
        Rune.IsControl(rune) && rune.Value is not ('\n' or '\r' or '\t');
}
