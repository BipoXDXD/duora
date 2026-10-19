using Duora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Profiles;

internal sealed class PreferenceRecord
{
    public Guid UserId { get; set; }
    public bool AllowDifferentDisplayLocales { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class CommunicationLanguageRecord
{
    public Guid UserId { get; set; }
    public string LanguageCode { get; set; } = "";
}

internal sealed class PreferenceRecordConfiguration : IEntityTypeConfiguration<PreferenceRecord>
{
    public void Configure(EntityTypeBuilder<PreferenceRecord> builder)
    {
        builder.ToTable("preferences", "profiles", table => table.HasCheckConstraint("ck_preferences_version", "version >= 1"));
        builder.HasKey(x => x.UserId).HasName("pk_preferences");
        builder.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(x => x.AllowDifferentDisplayLocales).HasColumnName("allow_different_display_locales");
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasOne<DuoraUser>().WithOne().HasForeignKey<PreferenceRecord>(x => x.UserId)
            .HasConstraintName("fk_preferences_users").OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CommunicationLanguageRecordConfiguration : IEntityTypeConfiguration<CommunicationLanguageRecord>
{
    public void Configure(EntityTypeBuilder<CommunicationLanguageRecord> builder)
    {
        builder.ToTable("communication_languages", "profiles", table =>
            table.HasCheckConstraint("ck_communication_languages_code", "language_code IN ('en', 'pt')"));
        builder.HasKey(x => new { x.UserId, x.LanguageCode }).HasName("pk_communication_languages");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.LanguageCode).HasColumnName("language_code");
        builder.HasOne<PreferenceRecord>().WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_communication_languages_preferences").OnDelete(DeleteBehavior.Restrict);
    }
}
