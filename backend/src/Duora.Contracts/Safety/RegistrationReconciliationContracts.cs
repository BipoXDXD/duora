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

/// <summary>
/// Criar um run a partir da prévia (API-ADM26, ADR-0049). <c>Units</c>: de 1 a 10 pares conta/medida com o
/// <c>UnitDigest</c> visto. <c>Justification</c> (até 2000 caracteres) e <c>AuthorizationReference</c> (até 200) ficam
/// numa linha, sem caracteres de controle, e não devem trazer dado de participante. O servidor recalcula o resto.
/// </summary>
public sealed record CreateRegistrationReconciliationRunRequest(
    IReadOnlyList<ReconciliationRunUnitRequest>? Units,
    string? Justification,
    string? AuthorizationReference);

/// <summary>Unidade pedida: a conta, a medida original e o digest devolvido pela prévia.</summary>
public sealed record ReconciliationRunUnitRequest(Guid SubjectUserId, Guid OriginalMeasureId, string? UnitDigest);

/// <summary>
/// Run de reconciliação (API-ADM26 e API-ADM27). <c>State</c>: Open | Closed. <c>ETag</c> identifica esta versão do
/// run. Instantes em UTC, com "Z". Sem recibo nesta etapa: nenhuma unidade é aplicada por estas rotas.
/// </summary>
public sealed record RegistrationReconciliationRunResponse(
    Guid Id,
    int SelectorVersion,
    string EvidenceId,
    DateTime CutoverLower,
    DateTime CutoverUpper,
    Guid CreatedByUserId,
    string Justification,
    string AuthorizationReference,
    string State,
    long Version,
    DateTime CreatedAt,
    string ETag,
    IReadOnlyList<RegistrationReconciliationRunUnitResponse> Units);

/// <summary>Unidade do manifesto. <c>State</c>: Pending | Applied | SkippedChanged | NeedsReview.</summary>
public sealed record RegistrationReconciliationRunUnitResponse(
    Guid Id,
    Guid SubjectUserId,
    Guid OriginalMeasureId,
    long MeasureVersion,
    string State,
    IReadOnlyList<ReconciliationPreviewItemResponse> Items);
