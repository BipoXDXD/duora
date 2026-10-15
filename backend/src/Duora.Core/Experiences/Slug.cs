namespace Duora.Core.Experiences;

/// <summary>Identificador legível estável (<c>unlikely-routes</c>, <c>route-stop-1</c>): a-z, 0-9 e hífen.</summary>
internal static class Slug
{
    public const int MaxLength = 64;

    public static string Require(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Deve ter de 1 a {MaxLength} caracteres [a-z0-9-], sem hífen nas pontas.", paramName);
        }

        return value;
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength
        && value[0] != '-'
        && value[^1] != '-'
        && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
