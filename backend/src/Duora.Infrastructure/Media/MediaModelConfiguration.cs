using Duora.Core.Media;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Media;

internal sealed class UploadConfiguration : IEntityTypeConfiguration<Upload>
{
    public void Configure(EntityTypeBuilder<Upload> builder)
    {
        var states = Quoted(Enum.GetNames<UploadState>());
        var contentTypes = Quoted(Enum.GetValues<PhotoFormat>().Select(PhotoFormats.ContentType));
        var rejections = Quoted(Enum.GetValues<PhotoRejection>().Select(PhotoRejections.Code));
        var pendingStates = Quoted(UploadLifecycle.PendingStates.Select(state => state.ToString()));
        builder.ToTable("uploads", DatabaseSchemas.Media, table =>
        {
            table.HasCheckConstraint("ck_uploads_state", $"state IN ({states})");
            table.HasCheckConstraint("ck_uploads_declared_content_type", $"declared_content_type IN ({contentTypes})");
            table.HasCheckConstraint("ck_uploads_size_bytes", $"size_bytes BETWEEN 1 AND {PhotoLimits.MaxBytes}");
            table.HasCheckConstraint("ck_uploads_client_key", "client_key ~ '^[A-Za-z0-9_-]{1,64}$'");
            // Hash só falta enquanto os bytes chegam; um Pending excluído (abandono) também não o tem.
            table.HasCheckConstraint(
                "ck_uploads_content_sha256",
                "(content_sha256 IS NULL OR octet_length(content_sha256) = 32) "
                + "AND (state IN ('Pending', 'Deleted') OR content_sha256 IS NOT NULL) "
                + "AND (state <> 'Pending' OR content_sha256 IS NULL)");
            // Código de rejeição só em Rejected, e mantido se a foto rejeitada for excluída depois.
            table.HasCheckConstraint(
                "ck_uploads_rejection_code",
                $"(rejection_code IS NULL OR rejection_code IN ({rejections})) "
                + "AND (state <> 'Rejected' OR rejection_code IS NOT NULL) "
                + "AND (rejection_code IS NULL OR state IN ('Rejected', 'Deleted'))");
            table.HasCheckConstraint("ck_uploads_version", "version >= 1");
        });
        builder.HasKey(u => u.Id).HasName("pk_uploads");

        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.OwnerId).HasColumnName("owner_id");
        builder.Property(u => u.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(u => u.ClientKey).HasColumnName("client_key").HasMaxLength(IdempotencyKey.MaxLength);
        builder.Property(u => u.QuarantineKey).HasColumnName("quarantine_key").HasMaxLength(128);
        builder.Property(u => u.DeclaredContentType).HasColumnName("declared_content_type").HasMaxLength(32);
        builder.Property(u => u.SizeBytes).HasColumnName("size_bytes");
        builder.Property(u => u.ContentSha256).HasColumnName("content_sha256");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.AbandonAfter).HasColumnName("abandon_after");
        builder.Property(u => u.RejectionCode).HasColumnName("rejection_code").HasMaxLength(32);
        builder.Property(u => u.Version).HasColumnName("version");

        builder.HasOne<DuoraUser>().WithMany().HasForeignKey(u => u.OwnerId)
            .HasConstraintName("fk_uploads_users_owner_id")
            .OnDelete(DeleteBehavior.Restrict);

        // Idempotência local (ADR-0025): a mesma chave do mesmo dono é o mesmo upload. A coluna líder também serve
        // de índice da FK do dono.
        builder.HasIndex(u => new { u.OwnerId, u.ClientKey }).IsUnique().HasDatabaseName("ux_uploads_owner_id_client_key");
        builder.HasIndex(u => u.QuarantineKey).IsUnique().HasDatabaseName("ux_uploads_quarantine_key");

        // Teto de pendentes (spec 11.5) e limpeza de abandonados: só as linhas ainda sem veredito.
        builder.HasIndex(u => u.OwnerId)
            .HasFilter($"state IN ({pendingStates})")
            .HasDatabaseName("ix_uploads_pending_owner");
        builder.HasIndex(u => u.AbandonAfter)
            .HasFilter("state IN ('Pending', 'Quarantined')")
            .HasDatabaseName("ix_uploads_abandon_after");
    }

    internal static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        var states = UploadConfiguration.Quoted(Enum.GetNames<MediaAssetState>());
        var detectedTypes = UploadConfiguration.Quoted(Enum.GetValues<PhotoFormat>().Select(PhotoFormats.ContentType));
        builder.ToTable("media_assets", DatabaseSchemas.Media, table =>
        {
            table.HasCheckConstraint("ck_media_assets_state", $"state IN ({states})");
            table.HasCheckConstraint("ck_media_assets_detected_type", $"detected_type IN ({detectedTypes})");
            table.HasCheckConstraint("ck_media_assets_dimensions", "width > 0 AND height > 0");
            table.HasCheckConstraint("ck_media_assets_pixels", $"width::bigint * height <= {PhotoInspection.MaxPixels}");
            table.HasCheckConstraint("ck_media_assets_sha256", "octet_length(sha256) = 32");
            table.HasCheckConstraint("ck_media_assets_version", "version >= 1");
        });
        builder.HasKey(a => a.Id).HasName("pk_media_assets");

        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.UploadId).HasColumnName("upload_id");
        builder.Property(a => a.OwnerId).HasColumnName("owner_id");
        builder.Property(a => a.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.DetectedType).HasColumnName("detected_type").HasMaxLength(32);
        builder.Property(a => a.Width).HasColumnName("width");
        builder.Property(a => a.Height).HasColumnName("height");
        builder.Property(a => a.Sha256).HasColumnName("sha256");
        builder.Property(a => a.OriginalKey).HasColumnName("original_key").HasMaxLength(128);
        builder.Property(a => a.ApprovedAt).HasColumnName("approved_at");
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.Version).HasColumnName("version");

        builder.HasOne<Upload>().WithOne().HasForeignKey<MediaAsset>(a => a.UploadId)
            .HasConstraintName("fk_media_assets_uploads_upload_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DuoraUser>().WithMany().HasForeignKey(a => a.OwnerId)
            .HasConstraintName("fk_media_assets_users_owner_id")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.UploadId).IsUnique().HasDatabaseName("ux_media_assets_upload_id");
        builder.HasIndex(a => a.OwnerId).HasDatabaseName("ix_media_assets_owner_id");
    }
}

internal sealed class MediaVariantConfiguration : IEntityTypeConfiguration<MediaVariant>
{
    public void Configure(EntityTypeBuilder<MediaVariant> builder)
    {
        var kinds = UploadConfiguration.Quoted(PhotoVariants.All.Select(PhotoVariants.Name));
        builder.ToTable("media_variants", DatabaseSchemas.Media, table =>
        {
            table.HasCheckConstraint("ck_media_variants_kind", $"kind IN ({kinds})");
            table.HasCheckConstraint("ck_media_variants_dimensions", "width > 0 AND height > 0");
            table.HasCheckConstraint("ck_media_variants_size_bytes", "size_bytes > 0");
        });
        builder.HasKey(v => v.Id).HasName("pk_media_variants");

        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.MediaAssetId).HasColumnName("media_asset_id");
        builder.Property(v => v.Kind).HasColumnName("kind").HasMaxLength(8);
        builder.Property(v => v.ObjectKey).HasColumnName("object_key").HasMaxLength(128);
        builder.Property(v => v.Width).HasColumnName("width");
        builder.Property(v => v.Height).HasColumnName("height");
        builder.Property(v => v.SizeBytes).HasColumnName("size_bytes");

        builder.HasOne<MediaAsset>().WithMany().HasForeignKey(v => v.MediaAssetId)
            .HasConstraintName("fk_media_variants_media_assets_media_asset_id")
            .OnDelete(DeleteBehavior.Cascade);

        // A unicidade por (asset, kind) também indexa a FK pela coluna líder.
        builder.HasIndex(v => new { v.MediaAssetId, v.Kind }).IsUnique().HasDatabaseName("ux_media_variants_media_asset_id_kind");
        builder.HasIndex(v => v.ObjectKey).IsUnique().HasDatabaseName("ux_media_variants_object_key");
    }
}

internal sealed class UploadGuardConfiguration : IEntityTypeConfiguration<UploadGuard>
{
    public void Configure(EntityTypeBuilder<UploadGuard> builder)
    {
        builder.ToTable("upload_guards", DatabaseSchemas.Media);
        builder.HasKey(g => g.OwnerId).HasName("pk_upload_guards");
        builder.Property(g => g.OwnerId).HasColumnName("owner_id").ValueGeneratedNever();

        // A guarda não sobrevive à conta: sem dono, não há envio a serializar.
        builder.HasOne<DuoraUser>().WithOne().HasForeignKey<UploadGuard>(g => g.OwnerId)
            .HasConstraintName("fk_upload_guards_users_owner_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
