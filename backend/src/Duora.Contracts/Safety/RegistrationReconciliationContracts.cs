namespace Duora.Contracts.Safety;

/// <summary>
/// Prévia da reconciliação de inscrições legadas (API-ADM25, issue #321), num único snapshot sem escrita. Só chaves
/// e contagens: nada de nome, e-mail ou motivo interno. <c>AsOf</c> em UTC, com "Z". <c>EvidenceId</c> é o
/// descritor de corte configurado no servidor, ou null quando não há corte (nada é candidato). As contagens cobrem
/// toda a população do snapshot; páginas pedidas em separado têm snapshots próprios.
/// </summary>
public sealed record RegistrationReconciliationPreviewResponse(
    DateTime AsOf,
    int SelectorVersion,
    string? EvidenceId,
    IReadOnlyList<ReconciliationIssueCountResponse> RegistrationsByPrimaryIssue,
    int ActionableRegistrations,
    int HeldRegistrations,
    ReconciliationUnitCountsResponse Units,
    IReadOnlyList<ReconciliationPreviewUnitResponse> Page,
    ReconciliationPreviewCursorResponse? Next);

/// <summary>Inscrições não candidatas por motivo principal (precedência do seletor), só os motivos presentes.</summary>
public sealed record ReconciliationIssueCountResponse(string Issue, int Registrations);

/// <summary>Unidades conta/medida: elegíveis, pausadas por integridade e grandes demais para aplicação automática.</summary>
public sealed record ReconciliationUnitCountsResponse(int Eligible, int Blocked, int Oversized);

/// <summary>
/// Conta/medida original com as inscrições candidatas, a versão observada da medida e o <c>UnitDigest</c>: SHA-256 em
/// hexadecimal minúsculo que a criação de um run confere contra o recálculo do servidor.
/// </summary>
public sealed record ReconciliationPreviewUnitResponse(
    Guid SubjectUserId,
    Guid OriginalMeasureId,
    long MeasureVersion,
    IReadOnlyList<ReconciliationPreviewItemResponse> Items,
    string UnitDigest);

/// <summary>
/// Inscrição candidata: versão observada, estado (Waitlisted | Confirmed | CheckedIn) e tetos condicionais de vagas
/// liberadas e de promoções.
/// </summary>
public sealed record ReconciliationPreviewItemResponse(
    Guid EventId,
    long RegistrationVersion,
    string State,
    int MaxReleasedSeats,
    int MaxPromotions);

/// <summary>Valores para <c>cursorSubject</c> e <c>cursorMeasure</c> da próxima página.</summary>
public sealed record ReconciliationPreviewCursorResponse(Guid CursorSubject, Guid CursorMeasure);
