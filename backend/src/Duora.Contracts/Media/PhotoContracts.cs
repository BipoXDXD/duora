namespace Duora.Contracts.Media;

// Saídas de API-M01 e API-M02 (ADR-0025). Nunca levam chave de objeto, bucket nem URL do storage (TM-42).

/// <summary>
/// Upload aceito. <c>Status</c>: Pending | Quarantined | Processing | Approved | Rejected | Deleted (spec 4.2); um
/// envio novo responde Quarantined, e a repetição com a mesma chave responde o estado atual do primeiro.
/// </summary>
public sealed record PhotoUploadResponse(Guid PhotoId, string Status);

/// <summary>Estado de processamento de uma foto do titular. <c>RejectionCode</c> só vem com Status Rejected.</summary>
public sealed record PhotoStatusResponse(Guid PhotoId, string Status, string? RejectionCode);
