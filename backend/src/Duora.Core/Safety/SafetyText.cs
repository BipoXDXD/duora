using System.Buffers;
using System.Text;

namespace Duora.Core.Safety;

/// <summary>
/// Validação dos textos livres do Safety (relato, motivo, recurso). Conta caracteres Unicode (runes), como a
/// spec 4.3 faz para mensagens, para o limite ser o mesmo no frontend e no backend.
/// </summary>
internal static class SafetyText
{
    public static string RequireBounded(string value, int maxLength, bool allowEmpty, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Não pode ser vazio.", paramName);
        }

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

    private static bool IsForbiddenControl(Rune rune) =>
        Rune.IsControl(rune) && rune.Value is not ('\n' or '\r' or '\t');
}
