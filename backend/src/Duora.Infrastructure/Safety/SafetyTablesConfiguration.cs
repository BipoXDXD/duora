using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Safety;

internal sealed class UserGuardConfiguration : IEntityTypeConfiguration<UserGuard>
{
    public void Configure(EntityTypeBuilder<UserGuard> builder)
    {
        builder.ToTable("user_guards", DatabaseSchemas.Safety, table =>
            table.HasCheckConstraint("ck_user_guards_security_version", "security_version >= 0"));
        builder.HasKey(g => g.UserId).HasName("pk_user_guards");

        builder.Property(g => g.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(g => g.SecurityVersion).HasColumnName("security_version").HasDefaultValue(0L);

        // A guarda não existe sem a conta (model.md: CASCADE); ela não guarda dado pessoal.
        builder.HasOne<DuoraUser>()
            .WithOne()
            .HasForeignKey<UserGuard>(g => g.UserId)
            .HasConstraintName("fk_user_guards_users")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoredBlockConfiguration : IEntityTypeConfiguration<StoredBlock>
{
    public void Configure(EntityTypeBuilder<StoredBlock> builder)
    {
        builder.ToTable("blocks", DatabaseSchemas.Safety, table =>
            table.HasCheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id"));
        builder.HasKey(b => b.Id).HasName("pk_blocks");

        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.BlockerId).HasColumnName("blocker_id");
        builder.Property(b => b.BlockedId).HasColumnName("blocked_id");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");

        // Bloqueio não duplicado (spec 10.3); a coluna líder também serve de índice da FK de quem bloqueou.
        builder.HasIndex(b => new { b.BlockerId, b.BlockedId }).IsUnique().HasDatabaseName("ux_blocks_pair");

        // Consulta bilateral "existe bloqueio entre A e B" e FK de quem foi bloqueado.
        builder.HasIndex(b => b.BlockedId).HasDatabaseName("ix_blocks_blocked_id");

        // RESTRICT (D6): a exclusão de conta remove os bloqueios por etapa, com trilha.
        builder.HasOne<DuoraUser>()
            .WithMany()
            .HasForeignKey(b => b.BlockerId)
            .HasConstraintName("fk_blocks_blocker")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DuoraUser>()
            .WithMany()
            .HasForeignKey(b => b.BlockedId)
            .HasConstraintName("fk_blocks_blocked")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
