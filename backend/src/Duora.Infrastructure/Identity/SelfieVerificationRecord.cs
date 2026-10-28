using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Identity;

internal sealed class SelfieVerificationRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public SelfieState State { get; set; }
    public string PolicyVersion { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string? FeedbackCode { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}

internal sealed class SelfieVerificationRecordConfiguration : IEntityTypeConfiguration<SelfieVerificationRecord>
{
    public void Configure(EntityTypeBuilder<SelfieVerificationRecord> builder)
    {
        builder.ToTable("selfie_verifications", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint("ck_selfie_state", "state IN ('Pending','Capturing','Processing','Passed','RecaptureRequired','Inconclusive','Expired')");
            table.HasCheckConstraint("ck_selfie_version", "version >= 0");
        });
        builder.HasKey(v => v.Id).HasName("pk_selfie_verifications");
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.UserId).HasColumnName("user_id");
        builder.Property(v => v.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32);
        builder.Property(v => v.PolicyVersion).HasColumnName("policy_version").HasMaxLength(64);
        builder.Property(v => v.ProviderReference).HasColumnName("provider_reference").HasMaxLength(200);
        builder.Property(v => v.FeedbackCode).HasColumnName("feedback_code").HasMaxLength(64);
        builder.Property(v => v.RequestedAt).HasColumnName("requested_at");
        builder.Property(v => v.ExpiresAt).HasColumnName("expires_at");
        builder.Property(v => v.CompletedAt).HasColumnName("completed_at");
        builder.Property(v => v.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<DuoraUser>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_selfie_verifications_users");
        builder.HasIndex(v => v.UserId).HasDatabaseName("ux_selfie_open_user").IsUnique().HasFilter("state IN ('Pending','Capturing','Processing')");
        builder.HasIndex(v => new { v.UserId, v.RequestedAt }).HasDatabaseName("ix_selfie_user_requested_at");
        builder.HasIndex(v => v.ProviderReference).HasDatabaseName("ux_selfie_provider_reference").IsUnique();
    }
}
