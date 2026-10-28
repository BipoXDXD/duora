using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Profiles;

/// <summary>
/// Linha de <c>profiles.profiles</c> (docs/data/model.md), dono Profiles. Mapeada só para o schema e a migration;
/// leitura e escrita são SQL direto em <see cref="ProfileStore"/>, porque a gravação condicional à versão precisa ser
/// um comando atômico.
/// </summary>
internal sealed class ProfileRecord
{
    private ProfileRecord()
    {
    }

    public Guid UserId { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Bio { get; private set; }

    /// <summary>País opcional; a subdivisão também é opcional e limitada pelo catálogo.</summary>
    public string? CountryCode { get; private set; }

    public string? SubdivisionCode { get; private set; }

    /// <summary>Token de concorrência exposto como ETag; sobe a cada gravação.</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

internal sealed class ProfileRecordConfiguration : IEntityTypeConfiguration<ProfileRecord>
{
    public void Configure(EntityTypeBuilder<ProfileRecord> builder)
    {
        builder.ToTable("profiles", DatabaseSchemas.Profiles, table =>
        {
            // char_length conta caracteres, como DisplayName e ProfileBio contam runes: o banco é a última barreira.
            table.HasCheckConstraint(
                "ck_profiles_display_name",
                $"char_length(display_name) BETWEEN 1 AND {DisplayName.MaxLength}");
            table.HasCheckConstraint("ck_profiles_bio", $"char_length(bio) BETWEEN 1 AND {ProfileBio.MaxLength}");
            table.HasCheckConstraint("ck_profiles_version", "version >= 1");
            table.HasCheckConstraint("ck_profiles_location_country", "subdivision_code IS NULL OR country_code IS NOT NULL");
        });
        builder.HasKey(p => p.UserId).HasName("pk_profiles");

        builder.Property(p => p.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasColumnName("display_name");
        builder.Property(p => p.Bio).HasColumnName("bio");
        builder.Property(p => p.CountryCode).HasColumnName("country_code");
        builder.Property(p => p.SubdivisionCode).HasColumnName("subdivision_code");
        builder.HasIndex(p => new { p.CountryCode, p.SubdivisionCode }).HasDatabaseName("ix_profiles_location");
        builder.HasOne<CountryRecord>().WithMany().HasForeignKey(p => p.CountryCode)
            .HasConstraintName("fk_profiles_countries").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SubdivisionRecord>().WithMany().HasForeignKey(p => new { p.CountryCode, p.SubdivisionCode })
            .HasPrincipalKey(p => new { p.CountryCode, p.Code })
            .HasConstraintName("fk_profiles_subdivisions").OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.Version).HasColumnName("version");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<DuoraUser>()
            .WithOne()
            .HasForeignKey<ProfileRecord>(p => p.UserId)
            .HasConstraintName("fk_profiles_users")
            // D6 (docs/data/model.md): a exclusão de conta do Privacy apaga módulo por módulo, sem cascata silenciosa.
            .OnDelete(DeleteBehavior.Restrict);
    }
}
