using Duora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Profiles;

internal sealed class RegionalPreferenceRecord
{
    public Guid UserId { get; set; }
    public string Mode { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RegionalPreferenceCountryRecord
{
    public Guid UserId { get; set; }
    public string CountryCode { get; set; } = "";
}

internal sealed class RegionalPreferenceRecordConfiguration : IEntityTypeConfiguration<RegionalPreferenceRecord>
{
    public void Configure(EntityTypeBuilder<RegionalPreferenceRecord> builder)
    {
        builder.ToTable("regional_preferences", "profiles", table =>
        {
            table.HasCheckConstraint("ck_regional_preferences_mode", "mode IN ('countries', 'unrestricted')");
            table.HasCheckConstraint("ck_regional_preferences_version", "version >= 1");
        });
        builder.HasKey(x => x.UserId).HasName("pk_regional_preferences");
        builder.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(x => x.Mode).HasColumnName("mode");
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasOne<DuoraUser>().WithOne().HasForeignKey<RegionalPreferenceRecord>(x => x.UserId)
            .HasConstraintName("fk_regional_preferences_users").OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RegionalPreferenceCountryRecordConfiguration : IEntityTypeConfiguration<RegionalPreferenceCountryRecord>
{
    public void Configure(EntityTypeBuilder<RegionalPreferenceCountryRecord> builder)
    {
        builder.ToTable("regional_preference_countries", "profiles");
        builder.HasKey(x => new { x.UserId, x.CountryCode }).HasName("pk_regional_preference_countries");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.CountryCode).HasColumnName("country_code");
        builder.HasIndex(x => x.CountryCode).HasDatabaseName("ix_regional_preference_countries_country_code");
        builder.HasOne<RegionalPreferenceRecord>().WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_regional_preference_countries_preference").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CountryRecord>().WithMany().HasForeignKey(x => x.CountryCode)
            .HasConstraintName("fk_regional_preference_countries_catalog").OnDelete(DeleteBehavior.Restrict);
    }
}
