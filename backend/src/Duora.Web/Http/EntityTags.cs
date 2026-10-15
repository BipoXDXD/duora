using System.Globalization;

namespace Duora.Web.Http;

/// <summary>O que um header <c>If-Match</c> pede (RFC 9110, 13.1.1), reduzido ao que a API aceita.</summary>
internal enum IfMatchKind
{
    /// <summary>Sem header, ou <c>*</c>: não identifica a versão lida, então não protege contra sobrescrita.</summary>
    Missing,

    /// <summary>Uma versão nossa, num ETag forte <c>"&lt;versão&gt;"</c>.</summary>
    Version,

    /// <summary>Qualquer outra coisa (ETag fraco, lista, valor alheio): nunca confere com a versão atual.</summary>
    Unmatchable,
}

internal readonly record struct IfMatch(IfMatchKind Kind, long Version);

/// <summary>
/// ETags de recursos versionados (spec 8.1): a versão do banco entre aspas, forte, porque <c>If-Match</c> usa
/// comparação forte. O valor é o mesmo que vai no corpo como <c>version</c>.
/// </summary>
internal static class EntityTags
{
    public static string Format(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static IfMatch ParseIfMatch(string? header)
    {
        var value = header?.Trim();
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return new IfMatch(IfMatchKind.Missing, 0);
        }

        var isQuotedDecimal = value.Length > 2
            && value[0] == '"'
            && value[^1] == '"'
            && value[1..^1].All(char.IsAsciiDigit)
            // Uma versão tem uma grafia só: "01" não é o ETag "1" que a API emitiu.
            && (value.Length == 3 || value[1] != '0');
        return isQuotedDecimal
            && long.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? new IfMatch(IfMatchKind.Version, version)
                : new IfMatch(IfMatchKind.Unmatchable, 0);
    }
}
