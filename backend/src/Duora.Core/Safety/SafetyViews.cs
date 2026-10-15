namespace Duora.Core.Safety;

/// <summary>
/// Projeção da denúncia para quem denunciou (API-S05): sem alvo, prioridade, moderador, evidência nem desfecho.
/// </summary>
public sealed record ReportStatusView(
    ReportId Protocol,
    ReportCategory Category,
    ReportStatusForReporter Status,
    DateTimeOffset SubmittedAt);

/// <summary>Projeção da medida para a pessoa sancionada.</summary>
public sealed record SanctionNoticeView(
    ModerationActionId ActionId,
    ModerationMeasure Measure,
    string Reason,
    PolicyVersion PolicyVersion,
    DateTimeOffset DecidedAt,
    DateTimeOffset? EffectiveUntil,
    bool DecidedAutomatically,
    bool CanAppeal);
