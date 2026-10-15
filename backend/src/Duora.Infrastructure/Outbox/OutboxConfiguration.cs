using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Outbox;

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("outbox_events", DatabaseSchemas.Ops);
        builder.HasKey(e => e.Id).HasName("pk_outbox_events");

        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(128).IsRequired();
        builder.Property(e => e.SchemaVersion).HasColumnName("schema_version");
        builder.Property(e => e.ResourceType).HasColumnName("resource_type").HasMaxLength(64).IsRequired();
        builder.Property(e => e.ResourceId).HasColumnName("resource_id");
        builder.Property(e => e.ResourceVersion).HasColumnName("resource_version");
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");

        builder.HasMany(e => e.Deliveries)
            .WithOne()
            .HasForeignKey(d => d.OutboxEventId)
            .HasConstraintName("fk_outbox_deliveries_outbox_events")
            // Apagar o evento (limpeza por retenção) leva junto as entregas, que não existem sem ele.
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Deliveries).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> builder)
    {
        var statuses = string.Join(", ", Enum.GetNames<OutboxDeliveryStatus>().Select(name => $"'{name}'"));
        builder.ToTable("outbox_deliveries", DatabaseSchemas.Ops, table =>
        {
            table.HasCheckConstraint("ck_outbox_deliveries_status", $"status IN ({statuses})");
            table.HasCheckConstraint("ck_outbox_deliveries_attempts", "attempts >= 0");
            table.HasCheckConstraint(
                "ck_outbox_deliveries_lease",
                "(status = 'InFlight') = (lease_expires_at IS NOT NULL)");
        });
        builder.HasKey(d => d.Id).HasName("pk_outbox_deliveries");

        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.OutboxEventId).HasColumnName("outbox_event_id");
        builder.Property(d => d.Consumer).HasColumnName("consumer").HasMaxLength(64).IsRequired();
        builder.Property(d => d.RecipientKey).HasColumnName("recipient_key").HasMaxLength(128).IsRequired();
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.Attempts).HasColumnName("attempts");
        builder.Property(d => d.AvailableAt).HasColumnName("available_at");
        builder.Property(d => d.DeadlineAt).HasColumnName("deadline_at");
        builder.Property(d => d.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(d => d.FencingToken).HasColumnName("fencing_token");
        builder.Property(d => d.LastError).HasColumnName("last_error").HasMaxLength(200);
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.CompletedAt).HasColumnName("completed_at");

        // Entrega identificável (spec 10.3); a coluna líder também serve de índice da FK.
        builder.HasIndex(d => new { d.OutboxEventId, d.Consumer, d.RecipientKey })
            .IsUnique()
            .HasDatabaseName("ux_outbox_deliveries_effect");

        // Fila do consumidor (spec 10.3): só as pendentes, na ordem em que ficam disponíveis.
        builder.HasIndex(d => new { d.Consumer, d.AvailableAt, d.Id })
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_outbox_deliveries_pending");

        // Leases vencidos, para outro worker retomar.
        builder.HasIndex(d => new { d.Consumer, d.LeaseExpiresAt })
            .HasFilter("status = 'InFlight'")
            .HasDatabaseName("ix_outbox_deliveries_in_flight");
    }
}
