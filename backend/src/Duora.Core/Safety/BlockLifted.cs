namespace Duora.Core.Safety;

/// <summary>Fato de auditoria do desbloqueio. A outra pessoa não é avisada.</summary>
public sealed record BlockLifted(Guid BlockerId, Guid BlockedId, DateTimeOffset LiftedAt);
