using System.Security.Cryptography;
using System.Text;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Senha temporária do bootstrap administrativo (D4): 24 símbolos do base32 de Crockford, sorteados pelo gerador
/// criptográfico do sistema (120 bits, dentro da política de 12 a 128). O alfabeto não tem I, L, O nem U, que se
/// confundem ao copiar do terminal; por isso a entrada é normalizada antes de conferir.
/// </summary>
public static class TemporaryPassword
{
    public const int Length = 24;

    private const string CrockfordBase32Symbols = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate() => RandomNumberGenerator.GetString(CrockfordBase32Symbols, Length);

    /// <summary>
    /// A forma canônica do que foi digitado, como o Crockford define: maiúsculas, O vira 0, I e L viram 1, e espaços
    /// e hífens de agrupamento somem. Uma senha errada continua errada; só a forma de digitar deixa de importar.
    /// </summary>
    public static string Normalize(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var canonical = new StringBuilder(typed.Length);
        foreach (var symbol in typed)
        {
            if (char.IsWhiteSpace(symbol) || symbol == '-')
            {
                continue;
            }

            canonical.Append(char.ToUpperInvariant(symbol) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var upper => upper,
            });
        }

        return canonical.ToString();
    }
}
