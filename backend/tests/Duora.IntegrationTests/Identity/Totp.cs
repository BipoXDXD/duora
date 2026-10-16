using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Gerador TOTP do lado do "aplicativo autenticador" (RFC 6238: HMAC-SHA1, passos de 30 s, 6 dígitos), a partir
/// da chave base32 que a API-A11 devolve. Implementado aqui só para o teste não depender do código testado.
/// </summary>
internal static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string sharedKey, DateTimeOffset at)
    {
        var counter = at.ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
#pragma warning disable CA5350 // SHA-1 é o algoritmo do TOTP (RFC 6238) que os autenticadores usam.
        var hash = HMACSHA1.HashData(DecodeBase32(sharedKey), message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Now(string sharedKey) => Code(sharedKey, DateTimeOffset.UtcNow);

    /// <summary>Um código que certamente não é o do passo atual nem dos vizinhos.</summary>
    public static string Wrong(string sharedKey)
    {
        var valid = new[] { -30, 0, 30 }.Select(s => Code(sharedKey, DateTimeOffset.UtcNow.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 10).Select(d => new string((char)('0' + d), 6)).First(c => !valid.Contains(c));
    }

    private static byte[] DecodeBase32(string value)
    {
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();
        foreach (var character in value.ToUpperInvariant().Where(c => c is not ('=' or ' ' or '-')))
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. output];
    }
}
