namespace Duora.Infrastructure.Audit;

/// <summary>
/// Linha de `ops.audit_events`, mapeada só para o modelo do EF gerar a migration. A escrita passa por
/// <see cref="AuditTrail"/>; ninguém altera nem apaga linha (D3).
/// </summary>
internal sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public long Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public AuditActorKind ActorKind { get; private set; }

    public Guid? ActorId { get; private set; }

    public string Action { get; private set; } = "";

    public string TargetType { get; private set; } = "";

    public Guid TargetId { get; private set; }

    public long? TargetVersion { get; private set; }

    public string? CorrelationId { get; private set; }
}
