using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Duora.Core.Identity;

/// <summary>
/// Verificação TOTP (RFC 6238: HMAC-SHA1, passos de 30 s, 6 dígitos), compatível com a chave base32 que o Identity
/// gera e com os aplicativos autenticadores. Existe porque o provider do Identity não diz qual passo aceitou, e sem o
/// passo não há como recusar o reuso do mesmo código (decisão da revisão do #38). Aceita só o passo atual e um de
/// cada lado (tolerância de relógio de 30 s).
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    private const int Modulo = 1_000_000;
    private const int AcceptedSkewSteps = 1;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static long StepAt(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / StepSeconds;

    public static string CodeAt(byte[] key, long step)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // SHA-1 é o algoritmo do TOTP (RFC 6238) que os autenticadores usam; aqui é HMAC.
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF;
        return (binary % Modulo).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Passo cujo código é <paramref name="code"/>, entre o atual e um de cada lado; null se nenhum.</summary>
    public static long? MatchStep(byte[] key, string code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var typed = Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);
        long? accepted = null;
        // Percorre a janela inteira para o tempo não dizer qual passo bateu.
        for (var step = current - AcceptedSkewSteps; step <= current + AcceptedSkewSteps; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(typed, Encoding.ASCII.GetBytes(CodeAt(key, step))))
            {
                accepted ??= step;
            }
        }

        return accepted;
    }

    /// <summary>O que a pessoa digitou, sem espaços (os apps mostram "123 456").</summary>
    public static string NormalizeInput(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return string.Concat(typed.Where(c => !char.IsWhiteSpace(c)));
    }

    /// <summary>Chave base32 (RFC 4648, sem padding obrigatório; espaços e caixa ignorados).</summary>
    /// <exception cref="FormatException">Chave vazia ou com caractere fora do alfabeto.</exception>
    public static byte[] DecodeBase32Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();
        foreach (var character in key.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '='))
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Chave base32 com caractere inválido.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return output.Count > 0 ? [.. output] : throw new FormatException("Chave base32 vazia.");
    }
}
