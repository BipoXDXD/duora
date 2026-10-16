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

    /// <summary>UF em ISO 3166-2:BR (<see cref="Core.Profiles.RegionCode"/>); <c>null</c> quando não informada.</summary>
    public string? RegionCode { get; private set; }

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
            // Conjunto fixo (27 UFs, muda só por emenda constitucional): CHECK, não lookup table (SQL 6 em model.md).
            table.HasCheckConstraint("ck_profiles_region_code", $"region_code IN ({RegionCodeList()})");
        });
        builder.HasKey(p => p.UserId).HasName("pk_profiles");

        builder.Property(p => p.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasColumnName("display_name");
        builder.Property(p => p.Bio).HasColumnName("bio");
        builder.Property(p => p.RegionCode).HasColumnName("region_code");
        builder.Property(p => p.Version).HasColumnName("version");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<DuoraUser>()
            .WithOne()
            .HasForeignKey<ProfileRecord>(p => p.UserId)
            .HasConstraintName("fk_profiles_users")
            // D6 (docs/data/model.md): a exclusão de conta do Privacy apaga módulo por módulo, sem cascata silenciosa.
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>Os códigos vêm da mesma lista do domínio; são constantes, sem aspas internas a escapar.</summary>
    private static string RegionCodeList() =>
        string.Join(", ", Core.Profiles.RegionCode.All.Select(code => $"'{code}'"));
}
