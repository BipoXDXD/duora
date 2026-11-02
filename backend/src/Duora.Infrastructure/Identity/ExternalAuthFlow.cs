using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Identity;

/// <summary>Validated provider identity; never an application principal or a permission grant.</summary>
public sealed record ExternalIdentity(string Subject, string Email);

/// <summary>The web process protects transient claims with the existing persistent Data Protection key ring.</summary>
public interface IExternalIdentityProtection
{
    string Protect(ExternalIdentity identity);
    ExternalIdentity? Unprotect(string protectedIdentity);
}

internal sealed class ExternalAuthFlow
{
    public Guid Id { get; set; }
    public byte[] BrowserHash { get; set; } = [];
    public string Purpose { get; set; } = "";
    public string Stage { get; set; } = "Created";
    public string? ProtectedProof { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? ExpectedStamp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
}

internal sealed class ExternalAuthFlowConfiguration : IEntityTypeConfiguration<ExternalAuthFlow>
{
    public void Configure(EntityTypeBuilder<ExternalAuthFlow> b)
    {
        b.ToTable("external_auth_flows", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint("ck_external_auth_flows_purpose", "purpose IN ('Login','Register','Link')");
            table.HasCheckConstraint("ck_external_auth_flows_stage", "stage IN ('Created','Challenged','Validated','Registration','Complete','Failed')");
            table.HasCheckConstraint("ck_external_auth_flows_window", "expires_at > created_at AND expires_at <= created_at + interval '5 minutes'");
            table.HasCheckConstraint("ck_external_auth_flows_binding", "octet_length(browser_hash)=32 AND attempts BETWEEN 0 AND 5");
        });
        b.HasKey(f => f.Id).HasName("pk_external_auth_flows");
        b.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(f => f.BrowserHash).HasColumnName("browser_hash");
        b.Property(f => f.Purpose).HasColumnName("purpose").HasMaxLength(16);
        b.Property(f => f.Stage).HasColumnName("stage").HasMaxLength(16);
        b.Property(f => f.ProtectedProof).HasColumnName("protected_proof");
        b.Property(f => f.UserId).HasColumnName("user_id");
        b.Property(f => f.SessionId).HasColumnName("session_id");
        b.Property(f => f.ExpectedStamp).HasColumnName("expected_stamp").HasMaxLength(256);
        b.Property(f => f.CreatedAt).HasColumnName("created_at");
        b.Property(f => f.ExpiresAt).HasColumnName("expires_at");
        b.Property(f => f.Attempts).HasColumnName("attempts");
        b.HasOne<DuoraUser>().WithMany().HasForeignKey(f => f.UserId).HasConstraintName("fk_external_auth_flows_users").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(f => f.ExpiresAt).HasDatabaseName("ix_external_auth_flows_expires_at");
        b.HasIndex(f => f.UserId).HasDatabaseName("ix_external_auth_flows_user_id");
    }
}
