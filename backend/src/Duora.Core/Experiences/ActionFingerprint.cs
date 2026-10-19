using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Duora.Core.Experiences;

/// <summary>
/// SHA-256 da forma canônica de uma ação (spec 8.7): o que o registro guarda para distinguir reenvio de reuso do
/// <see cref="ActionId"/> sem manter o corpo, que pode ser uma escolha privada (INV-05). É o
/// <c>game_actions.payload_sha256</c> do ADR-0020, então o formato é contrato: o mesmo valor em qualquer processo,
/// cultura e release.
/// </summary>
/// <remarks>
/// <para>
/// Forma canônica v1: sequência de netstrings (<c>&lt;bytes UTF-8 em decimal&gt;:&lt;conteúdo&gt;,</c>) com o rótulo
/// <c>duora.game-action/v1</c>, o <see cref="IGameAction.Type"/>, a quantidade de campos e, para cada propriedade
/// pública da ação em ordem ordinal de nome, o nome e o valor. Valores: <c>s:&lt;texto&gt;</c> para string,
/// <c>e:&lt;valor declarado&gt;</c> para enum e <c>null</c> para ausência. O prefixo de tamanho impede que mover
/// caracteres de um campo para outro produza a mesma sequência.
/// </para>
/// <para>
/// Não usa <see cref="object.GetHashCode"/> (aleatório por processo para string) nem serialização de biblioteca
/// (escape e ordem de propriedades podem mudar entre versões). Campo de outro tipo falha alto: incluí-lo é decidir
/// como ele vira texto, e isso muda o formato. Renomear um campo de ação também muda o hash; um reenvio feito
/// através desse deploy é recusado como reuso do id, e o cliente gera um id novo.
/// </para>
/// </remarks>
public sealed record ActionFingerprint
{
    private const string FormatLabel = "duora.game-action/v1";

    private ActionFingerprint(string sha256Hex) => Sha256Hex = sha256Hex;

    /// <summary>Hash em hexadecimal minúsculo, 64 caracteres.</summary>
    public string Sha256Hex { get; }

    internal static ActionFingerprint Restore(string sha256Hex)
    {
        if (sha256Hex.Length != 64 || sha256Hex.Any(c => !char.IsAsciiHexDigitLower(c)))
        {
            throw new ArgumentException("Invalid SHA-256 fingerprint.", nameof(sha256Hex));
        }

        return new ActionFingerprint(sha256Hex);
    }

    public static ActionFingerprint Of(IGameAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm(action)));
        return new ActionFingerprint(Convert.ToHexStringLower(hash));
    }

    private static string CanonicalForm(IGameAction action)
    {
        var fields = action.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.Name != nameof(IGameAction.Type) && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToList();

        var canonical = new StringBuilder();
        AppendNetstring(canonical, FormatLabel);
        AppendNetstring(canonical, action.Type.Value);
        AppendNetstring(canonical, fields.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var field in fields)
        {
            AppendNetstring(canonical, field.Name);
            AppendNetstring(canonical, CanonicalValue(action, field));
        }

        return canonical.ToString();
    }

    private static string CanonicalValue(IGameAction action, PropertyInfo field) => field.GetValue(action) switch
    {
        null => "null",
        string text => "s:" + text,
        Enum value => "e:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"Campo '{field.Name}' de '{action.GetType().Name}' é {field.PropertyType.Name}; a forma canônica v1 só "
            + "aceita string e enum."),
    };

    private static void AppendNetstring(StringBuilder canonical, string content) =>
        canonical
            .Append(Encoding.UTF8.GetByteCount(content).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(content)
            .Append(',');
}
