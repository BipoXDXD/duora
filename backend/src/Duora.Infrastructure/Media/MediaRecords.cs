using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Linha de <c>media.uploads</c> (model.md). Nasce <see cref="UploadState.Pending"/> com a vaga reservada; as transições
/// seguintes são <c>UPDATE</c> condicional pelo estado e pela versão, para dois processos não aplicarem a mesma duas vezes.
/// </summary>
internal sealed class Upload
{
    /// <summary>Prazo para abandonar um upload que não chegou a um veredito (spec 12.4; limpeza a implementar).</summary>
    public static readonly TimeSpan AbandonAfterDelay = TimeSpan.FromHours(24);

    private Upload()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public UploadState State { get; private set; }

    /// <summary>Chave de idempotência do cliente, única por dono.</summary>
    public string ClientKey { get; private set; } = "";

    /// <summary>Chave do original na quarentena, gerada pelo servidor. Nunca sai da API (TM-42).</summary>
    public string QuarantineKey { get; private set; } = "";

    public string DeclaredContentType { get; private set; } = "";

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 dos bytes recebidos; nulo só enquanto <c>Pending</c>.</summary>
    public byte[]? ContentSha256 { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset AbandonAfter { get; private set; }

    public string? RejectionCode { get; private set; }

    public long Version { get; private set; }

    public static Upload Reserve(Guid ownerId, IdempotencyKey key, PhotoFormat format, long sizeBytes, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OwnerId = ownerId,
        State = UploadState.Pending,
        ClientKey = key.Value,
        QuarantineKey = ObjectKey.NewQuarantine().Value,
        DeclaredContentType = PhotoFormats.ContentType(format),
        SizeBytes = sizeBytes,
        CreatedAt = now,
        AbandonAfter = now + AbandonAfterDelay,
        Version = 1,
    };

    public UploadFingerprint? Fingerprint() =>
        ContentSha256 is { } hash && PhotoFormats.TryParseContentType(DeclaredContentType, out var format)
            ? new UploadFingerprint(format, SizeBytes, hash)
            : null;
}

internal enum MediaAssetState
{
    Approved,
    Rejected,
    Deleted,
}

/// <summary>Foto aprovada (<c>media.media_assets</c>); o <c>Id</c> é o <c>assetId</c> de API-M04.</summary>
internal sealed class MediaAsset
{
    private MediaAsset()
    {
    }

    public Guid Id { get; private set; }

    public Guid UploadId { get; private set; }

    public Guid OwnerId { get; private set; }

    public MediaAssetState State { get; private set; }

    public string DetectedType { get; private set; } = "";

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>SHA-256 do original verificado, que é apagado depois da derivação.</summary>
    public byte[] Sha256 { get; private set; } = [];

    /// <summary>Chave do original; nula depois que ele é apagado (spec 12.4).</summary>
    public string? OriginalKey { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public long Version { get; private set; }

    public static MediaAsset Approve(
        Guid uploadId,
        Guid ownerId,
        PhotoFormat detected,
        int width,
        int height,
        ReadOnlySpan<byte> sha256,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            UploadId = uploadId,
            OwnerId = ownerId,
            State = MediaAssetState.Approved,
            DetectedType = PhotoFormats.ContentType(detected),
            Width = width,
            Height = height,
            Sha256 = sha256.ToArray(),
            ApprovedAt = now,
            Version = 1,
        };
}

/// <summary>Derivado reencodado de uma foto aprovada (<c>media.media_variants</c>).</summary>
internal sealed class MediaVariant
{
    private MediaVariant()
    {
    }

    public Guid Id { get; private set; }

    public Guid MediaAssetId { get; private set; }

    public string Kind { get; private set; } = "";

    public string ObjectKey { get; private set; } = "";

    public int Width { get; private set; }

    public int Height { get; private set; }

    public long SizeBytes { get; private set; }

    public static MediaVariant Create(
        Guid mediaAssetId,
        PhotoVariant kind,
        ObjectKey key,
        int width,
        int height,
        long sizeBytes,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            MediaAssetId = mediaAssetId,
            Kind = PhotoVariants.Name(kind),
            ObjectKey = key.Value,
            Width = width,
            Height = height,
            SizeBytes = sizeBytes,
        };
}

/// <summary>
/// Guarda de envio por dono (<c>media.upload_guards</c>, ADR-0025): uma linha por conta que já enviou foto, só para
/// ser travada com <c>FOR UPDATE</c> na reserva. Não guarda estado; o que importa é o lock.
/// </summary>
internal sealed class UploadGuard
{
    private UploadGuard()
    {
    }

    public Guid OwnerId { get; private set; }
}
