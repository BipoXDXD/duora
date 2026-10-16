using System.Diagnostics.CodeAnalysis;

namespace Duora.Core.Media;

public enum UploadLengthVerdict
{
    Accepted,

    /// <summary>Sem <c>Content-Length</c> (inclusive chunked): o limite não pode ser conferido antes de ler.</summary>
    Missing,

    Empty,

    TooLarge,
}

/// <summary>Limites da recepção de foto (spec 4.3).</summary>
public static class PhotoLimits
{
    /// <summary>Foto: até 5 MiB de corpo.</summary>
    public const long MaxBytes = 5L * 1024 * 1024;

    public static UploadLengthVerdict CheckContentLength(long? contentLength) => contentLength switch
    {
        null => UploadLengthVerdict.Missing,
        <= 0 => UploadLengthVerdict.Empty,
        > MaxBytes => UploadLengthVerdict.TooLarge,
        _ => UploadLengthVerdict.Accepted,
    };
}

/// <summary>
/// Chave de idempotência do cliente (header <c>Idempotency-Key</c>, spec 8.7): de 1 a 64 caracteres ASCII seguros
/// para log e banco. Um UUID cabe; texto livre, espaço e caractere de controle, não.
/// </summary>
public readonly record struct IdempotencyKey
{
    public const int MaxLength = 64;

    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out IdempotencyKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !value.All(IsAllowed))
        {
            return false;
        }

        key = new IdempotencyKey(value);
        return true;
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}

/// <summary>Teto de uploads ainda sem veredito por conta (spec 11.5). O valor é hipótese configurável.</summary>
public sealed record PendingUploadQuota
{
    public const int DefaultCap = 3;

    public PendingUploadQuota(int cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        Cap = cap;
    }

    public int Cap { get; }

    public bool Admits(int pendingUploads) => pendingUploads < Cap;
}
