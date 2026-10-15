using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Audit;

/// <summary>
/// `ops.audit_events` (model.md, D3). Sem FKs de propósito: o registro sobrevive ao alvo e ao autor. Motivo, versão
/// da política e metadata entram com a moderação, por migration aditiva.
/// </summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public const int ActionMaxLength = 64;
    public const int TargetTypeMaxLength = 32;

    /// <summary>Trace id do W3C em hexadecimal (32 caracteres).</summary>
    public const int CorrelationIdMaxLength = 32;

    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        var actorKinds = string.Join(", ", Enum.GetNames<AuditActorKind>().Select(name => $"'{name}'"));
        builder.ToTable("audit_events", DatabaseSchemas.Ops, table =>
        {
            table.HasCheckConstraint("ck_audit_events_actor_kind", $"actor_kind IN ({actorKinds})");
            table.HasCheckConstraint(
                "ck_audit_events_actor_id",
                "actor_kind = 'System' OR actor_id IS NOT NULL");
            table.HasCheckConstraint("ck_audit_events_action", "action ~ '^[a-z]+(\\.[a-z_]+)+$'");
            table.HasCheckConstraint("ck_audit_events_target_type", "target_type ~ '^[a-z][a-z_]{0,31}$'");
        });
        builder.HasKey(e => e.Id).HasName("pk_audit_events");

        builder.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.ActorId).HasColumnName("actor_id");
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(ActionMaxLength).IsRequired();
        builder.Property(e => e.TargetType).HasColumnName("target_type").HasMaxLength(TargetTypeMaxLength).IsRequired();
        builder.Property(e => e.TargetId).HasColumnName("target_id");
        builder.Property(e => e.TargetVersion).HasColumnName("target_version");
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(CorrelationIdMaxLength);

        // Consultas da moderação e do titular (model.md): histórico de um alvo e ações de um ator.
        builder.HasIndex(e => new { e.TargetType, e.TargetId, e.OccurredAt }).HasDatabaseName("ix_audit_events_target");
        builder.HasIndex(e => new { e.ActorId, e.OccurredAt }).HasDatabaseName("ix_audit_events_actor");
    }
}
