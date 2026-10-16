namespace Duora.Core.Media;

public enum IdempotentOutcome
{
    /// <summary>Mesmo pedido: devolve o resultado do primeiro, sem novo efeito.</summary>
    Replay,

    /// <summary>Mesma chave com outro conteúdo: <c>409 IDEMPOTENCY_KEY_REUSED</c> (spec 8.7).</summary>
    KeyReused,
}

/// <summary>
/// O que torna dois envios "o mesmo pedido" (spec 8.7): tipo declarado, tamanho e SHA-256 dos bytes. É o hash
/// canônico dos dados relevantes de um upload, que não tem outro campo.
/// </summary>
public sealed record UploadFingerprint
{
    public const int Sha256Length = 32;

    private readonly byte[] _sha256;

    public UploadFingerprint(PhotoFormat format, long sizeBytes, ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != Sha256Length)
        {
            throw new ArgumentException($"SHA-256 tem {Sha256Length} bytes.", nameof(sha256));
        }

        Format = format;
        SizeBytes = sizeBytes;
        _sha256 = sha256.ToArray();
    }

    public PhotoFormat Format { get; }

    public long SizeBytes { get; }

    public ReadOnlyMemory<byte> Sha256 => _sha256;

    public static IdempotentOutcome Compare(UploadFingerprint stored, UploadFingerprint incoming) =>
        stored.Format == incoming.Format
        && stored.SizeBytes == incoming.SizeBytes
        && stored._sha256.AsSpan().SequenceEqual(incoming._sha256)
            ? IdempotentOutcome.Replay
            : IdempotentOutcome.KeyReused;

    public bool Equals(UploadFingerprint? other) => other is not null && Compare(this, other) == IdempotentOutcome.Replay;

    public override int GetHashCode() => HashCode.Combine(Format, SizeBytes, BitConverter.ToInt32(_sha256));
}
