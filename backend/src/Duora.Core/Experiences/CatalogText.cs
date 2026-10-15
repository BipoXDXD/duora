namespace Duora.Core.Experiences;

/// <summary>Texto curto de catálogo exibido ao participante: obrigatório, limitado e sem caracteres de controle.</summary>
internal static class CatalogText
{
    public static string Require(string value, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Deve ter texto visível, até {maxLength} caracteres e nenhum caractere de controle.", paramName);
        }

        return value;
    }
}
