using System.Security.Cryptography;
using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Seção <c>Media:Deletion</c>: escopo e material dedicado da chave de operação V1 de API-M03
/// (<see cref="PhotoDeletionIntentBinding"/>). O material é segredo, vem de arquivo ou variável de ambiente e precisa
/// ficar estável entre retries e restores; trocar o ID ou o material exige protocolo explícito de custódia.
/// </summary>
internal sealed class PhotoDeletionOptions
{
    public const string Section = "Media:Deletion";

    private const int MinKeyBytes = 32;
    private const int MaxKeyBytes = 64;

    public Guid EnvironmentId { get; set; }

    public string OperationKeyId { get; set; } = "";

    /// <summary>Material da HMAC em base64, de 32 a 64 bytes. Nunca vai a log, DTO ou mensagem de erro.</summary>
    public string OperationKey { get; set; } = "";

    public bool IsComplete()
    {
        if (EnvironmentId == Guid.Empty || !IdempotencyKey.TryParse(OperationKeyId, out _))
        {
            return false;
        }

        var key = TryReadKey();
        if (key is null)
        {
            return false;
        }

        var length = key.Length;
        CryptographicOperations.ZeroMemory(key);
        return length is >= MinKeyBytes and <= MaxKeyBytes;
    }

    /// <summary>Cópia nova do material, ou nulo se não for base64; quem recebe zera depois de usar.</summary>
    public byte[]? TryReadKey()
    {
        var buffer = new byte[(OperationKey.Length * 3 / 4) + 3];
        try
        {
            return Convert.TryFromBase64String(OperationKey, buffer, out var written) && written > 0
                ? buffer[..written]
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
