using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Identity;

/// <summary>De onde veio a decisão de uma solicitação (coluna <c>decision_source</c>).</summary>
internal enum AgeDecisionSource
{
    Callback,
    ProviderQuery,
    ManualReview,
}

/// <summary>
/// Linha de <c>identity.age_verifications</c> (model.md), mapeada para o EF gerar a migration e para leituras. Toda
/// escrita é SQL condicional no <see cref="AgeVerificationService"/> e no worker, conferindo o estado: o EF não
/// grava esta entidade. Sem documento, imagem, biometria ou data de nascimento (spec 12.1, 12.2).
/// </summary>
internal sealed class AgeVerificationRecord
{
    public const int MaxProviderLength = 32;
    public const int MaxNonceLength = 64;

    private AgeVerificationRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public AgeVerificationState State { get; private set; }

    public string PolicyVersion { get; private set; } = "";

    public string Provider { get; private set; } = "";

    /// <summary>Referência do fornecedor, gravada pelo worker quando a verificação é aberta lá.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Gerado pelo servidor; o callback precisa trazê-lo (TM-36).</summary>
    public string RequestNonce { get; private set; } = "";

    /// <summary>Fluxo do fornecedor para onde a pessoa segue; só https e dentro da allowlist do adaptador.</summary>
    public string? ProviderFlowUrl { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>
    /// Obsoleta desde a verificação única no cadastro (ADR-0024, adendo de 2026-10-16): não é mais gravada nem lida.
    /// Fica no modelo só para a coluna sobreviver a esta release (expand/contract); sai num PR seguinte.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public AgeDecisionSource? DecisionSource { get; private set; }

    public long Version { get; private set; }
}

internal sealed class AgeVerificationRecordConfiguration : IEntityTypeConfiguration<AgeVerificationRecord>
{
    /// <summary>Solicitações abertas; no máximo uma por pessoa (ADR-0024, decisão 6).</summary>
    public const string OpenStatesFilter = "state IN ('Pending', 'ManualReview')";

    public void Configure(EntityTypeBuilder<AgeVerificationRecord> builder)
    {
        var states = string.Join(", ", Enum.GetNames<AgeVerificationState>().Select(name => $"'{name}'"));
        var sources = string.Join(", ", Enum.GetNames<AgeDecisionSource>().Select(name => $"'{name}'"));
        builder.ToTable("age_verifications", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint("ck_age_verifications_state", $"state IN ({states})");
            table.HasCheckConstraint(
                "ck_age_verifications_decision_source",
                $"decision_source IS NULL OR decision_source IN ({sources})");
            table.HasCheckConstraint(
                "ck_age_verifications_flow_url",
                "provider_flow_url IS NULL OR provider_flow_url LIKE 'https://%'");
            table.HasCheckConstraint("ck_age_verifications_version", "version >= 0");
        });
        builder.HasKey(v => v.Id).HasName("pk_age_verifications");

        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.UserId).HasColumnName("user_id");
        builder.Property(v => v.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.PolicyVersion).HasColumnName("policy_version")
            .HasMaxLength(AgeVerificationPolicy.MaxVersionLength).IsRequired();
        builder.Property(v => v.Provider).HasColumnName("provider").HasMaxLength(AgeVerificationRecord.MaxProviderLength).IsRequired();
        builder.Property(v => v.ProviderReference).HasColumnName("provider_reference")
            .HasMaxLength(AgeCheckSession.MaxReferenceLength);
        builder.Property(v => v.RequestNonce).HasColumnName("request_nonce").HasMaxLength(AgeVerificationRecord.MaxNonceLength).IsRequired();
        builder.Property(v => v.ProviderFlowUrl).HasColumnName("provider_flow_url")
            .HasMaxLength(AgeCheckSession.MaxFlowUrlLength);
        builder.Property(v => v.RequestedAt).HasColumnName("requested_at");
        builder.Property(v => v.DecidedAt).HasColumnName("decided_at");
        builder.Property(v => v.ExpiresAt).HasColumnName("expires_at");
        builder.Property(v => v.DecisionSource).HasColumnName("decision_source").HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.Version).HasColumnName("version");

        builder.HasOne<DuoraUser>()
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .HasConstraintName("fk_age_verifications_users")
            // O resultado é evidência (TM-37): a conta não some levando o histórico; a exclusão (spec 12.4) trata
            // estas linhas antes, pelo processo de retenção.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => new { v.Provider, v.ProviderReference })
            .IsUnique()
            .HasDatabaseName("ux_age_verifications_provider_provider_reference");
        builder.HasIndex(v => v.RequestNonce).IsUnique().HasDatabaseName("ux_age_verifications_request_nonce");
        builder.HasIndex(v => v.UserId)
            .IsUnique()
            .HasFilter(OpenStatesFilter)
            .HasDatabaseName("ux_age_verifications_open_user");
        // Índice da FK e da leitura "última solicitação da pessoa".
        builder.HasIndex(v => new { v.UserId, v.RequestedAt }).HasDatabaseName("ix_age_verifications_user_id_requested_at");
    }
}
