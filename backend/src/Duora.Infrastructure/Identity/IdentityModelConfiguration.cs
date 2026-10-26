using System.Text;
using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Leva as tabelas do ASP.NET Core Identity para o schema <c>identity</c> com nomes snake_case (spec 10.1). As
/// tabelas e chaves são nomeadas aqui; as colunas seguem o nome da propriedade convertido, porque as entidades são
/// do framework e não há outro lugar para mapeá-las uma a uma.
/// </summary>
internal static class IdentityModelConfiguration
{
    private static readonly (Type Entity, string Table)[] IdentityTables =
    [
        (typeof(DuoraUser), "users"),
        (typeof(IdentityRole<Guid>), "roles"),
        (typeof(IdentityUserRole<Guid>), "user_roles"),
        (typeof(IdentityUserClaim<Guid>), "user_claims"),
        (typeof(IdentityUserLogin<Guid>), "user_logins"),
        (typeof(IdentityUserToken<Guid>), "user_tokens"),
        (typeof(IdentityRoleClaim<Guid>), "role_claims"),
    ];

    /// <summary>Chamar depois do <c>base.OnModelCreating</c> do IdentityDbContext, que cria os nomes padrão.</summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DuoraUser>(ConfigureUser);
        modelBuilder.Entity<IdentityRole<Guid>>(ConfigureRoles);

        foreach (var (entity, table) in IdentityTables)
        {
            modelBuilder.Entity(entity).ToTable(table, DatabaseSchemas.Identity);
            RenameStoreObjects(modelBuilder.Model.FindEntityType(entity)
                ?? throw new InvalidOperationException($"{entity} não está no modelo."), table);
        }
    }

    /// <summary>
    /// Catálogo fixo de papéis (model.md). Ids fixos para a migration ser determinística; nenhum usuário nem
    /// permissão vem por seed (spec 17).
    /// </summary>
    private static readonly (Guid Id, string Name)[] SeededRoles =
    [
        (new Guid("01920000-0000-7000-8000-000000000001"), AccountRoles.Participant),
        (new Guid("01920000-0000-7000-8000-000000000002"), AccountRoles.Moderator),
        (new Guid("01920000-0000-7000-8000-000000000003"), AccountRoles.ProductAdmin),
        (new Guid("01920000-0000-7000-8000-000000000004"), AccountRoles.Operator),
    ];

    private static void ConfigureRoles(EntityTypeBuilder<IdentityRole<Guid>> role) =>
        role.HasData(SeededRoles.Select(seed => new IdentityRole<Guid>
        {
            Id = seed.Id,
            Name = seed.Name,
            NormalizedName = seed.Name.ToUpperInvariant(),
            ConcurrencyStamp = seed.Id.ToString("D"),
        }));

    private static void ConfigureUser(EntityTypeBuilder<DuoraUser> user)
    {
        var statuses = string.Join(", ", Enum.GetNames<AccountStatus>().Select(name => $"'{name}'"));
        user.ToTable(table =>
        {
            table.HasCheckConstraint("ck_users_preferred_locale", "preferred_locale IN ('en-US', 'pt-BR')");
            table.HasCheckConstraint("ck_users_locale_version", "locale_version >= 0");
            table.HasCheckConstraint("ck_users_status", $"status IN ({statuses})");
            // O nome de usuário é o e-mail. Com isso, o índice único do e-mail garante também o do nome, e o
            // upsert do cadastro arbitra um índice só (com dois índices únicos, cadastros simultâneos do mesmo
            // e-mail estourariam no índice que o ON CONFLICT não arbitra).
            table.HasCheckConstraint(
                "ck_users_user_name_is_email",
                "normalized_user_name IS NOT DISTINCT FROM normalized_email");
            // Conta com senha temporária (bootstrap administrativo) nasce confirmada e assim fica: o upsert do
            // cadastro só substitui conta não confirmada, então nunca toca uma conta privilegiada pendente (TM-66).
            table.HasCheckConstraint(
                "ck_users_temporary_password_requires_confirmed_email",
                "temporary_password_expires_at IS NULL OR email_confirmed");
        });
        user.Property(u => u.Status).HasConversion<string>().HasMaxLength(24);
        user.Property(u => u.CreatedAt);
        user.Property(u => u.PreferredLocale);
        user.Property(u => u.LocaleVersion).HasDefaultValue(0L);
        user.Property(u => u.PreferredTimeZone).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        user.Property(u => u.TimeZoneVersion).HasDefaultValue(0L).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        user.Property(u => u.LastTotpStep);
        user.Property(u => u.TemporaryPasswordExpiresAt);
        user.Property(u => u.Email).HasMaxLength(EmailAddress.MaxLength);
        user.Property(u => u.NormalizedEmail).HasMaxLength(EmailAddress.MaxLength);
        user.Property(u => u.UserName).HasMaxLength(EmailAddress.MaxLength);
        user.Property(u => u.NormalizedUserName).HasMaxLength(EmailAddress.MaxLength);

        // O Identity cria o índice de e-mail sem unicidade; aqui ele vira a garantia contra cadastro duplicado
        // concorrente, que a verificação prévia do UserManager não impede.
        user.HasIndex(u => u.NormalizedEmail).IsUnique();
        user.HasIndex(u => u.NormalizedUserName).IsUnique(false);
    }

    private static void RenameStoreObjects(IMutableEntityType entity, string table)
    {
        foreach (var property in entity.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }

        entity.FindPrimaryKey()?.SetName($"pk_{table}");

        foreach (var foreignKey in entity.GetForeignKeys())
        {
            foreignKey.SetConstraintName($"fk_{table}_{foreignKey.PrincipalEntityType.GetTableName()}");
        }

        foreach (var index in entity.GetIndexes())
        {
            var prefix = index.IsUnique ? "ux" : "ix";
            var columns = string.Join("_", index.Properties.Select(p => ToSnakeCase(p.Name)));
            index.SetDatabaseName($"{prefix}_{table}_{columns}");
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character) && i > 0 && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        var documents = string.Join(", ", Enum.GetNames<LegalDocument>().Select(name => $"'{name}'"));
        builder.ToTable("consent_records", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint("ck_consent_records_document", $"document IN ({documents})");
            table.HasCheckConstraint("ck_consent_records_document_sha256", "document_sha256 ~ '^[a-f0-9]{64}$'");
            table.HasCheckConstraint("ck_consent_records_document_locale", "document_locale IN ('pt-BR', 'en-US')");
        });
        builder.HasKey(c => c.Id).HasName("pk_consent_records");

        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.UserId).HasColumnName("user_id");
        builder.Property(c => c.Document).HasColumnName("document").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Version).HasColumnName("version").HasMaxLength(32).IsRequired();
        builder.Property(c => c.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(c => c.DocumentSha256).HasColumnName("document_sha256").HasColumnType("text");
        builder.Property(c => c.DocumentLocale).HasColumnName("document_locale").HasColumnType("text");

        builder.HasOne<DuoraUser>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .HasConstraintName("fk_consent_records_users")
            // Os aceites existem por causa da conta; a exclusão da conta (spec 12.4) os leva junto.
            .OnDelete(DeleteBehavior.Cascade);

        // A coluna líder também serve de índice da FK.
        builder.HasIndex(c => new { c.UserId, c.Document, c.Version })
            .IsUnique()
            .HasDatabaseName("ux_consent_records_user_id_document_version");
    }
}

internal sealed class LoginSessionConfiguration : IEntityTypeConfiguration<LoginSession>
{
    public void Configure(EntityTypeBuilder<LoginSession> builder)
    {
        builder.ToTable("login_sessions", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint("ck_login_sessions_window", "idle_expires_at <= absolute_expires_at");
            table.HasCheckConstraint("ck_login_sessions_key_hash", "octet_length(key_hash) = 32");
            var policies = string.Join(", ", Enum.GetNames<SessionPolicy>().Select(name => $"'{name}'"));
            table.HasCheckConstraint("ck_login_sessions_policy", $"policy IN ({policies})");
        });
        builder.HasKey(s => s.Id).HasName("pk_login_sessions");

        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.UserId).HasColumnName("user_id");
        builder.Property(s => s.KeyHash).HasColumnName("key_hash");
        builder.Property(s => s.Ticket).HasColumnName("ticket");
        builder.Property(s => s.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(256).IsRequired();
        builder.Property(s => s.Description).HasColumnName("description").HasMaxLength(64).IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(s => s.IdleExpiresAt).HasColumnName("idle_expires_at");
        builder.Property(s => s.AbsoluteExpiresAt).HasColumnName("absolute_expires_at");
        builder.Property(s => s.Policy).HasColumnName("policy").HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.ReauthenticatedAt).HasColumnName("reauthenticated_at");
        builder.Property(s => s.MfaVerifiedAt).HasColumnName("mfa_verified_at");
        builder.Property(s => s.TotpVerifiedAt).HasColumnName("totp_verified_at");
        builder.Property(s => s.RevokedAt).HasColumnName("revoked_at");
        builder.Property(s => s.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(32);

        builder.HasOne<DuoraUser>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .HasConstraintName("fk_login_sessions_users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.KeyHash).IsUnique().HasDatabaseName("ux_login_sessions_key_hash");
        builder.HasIndex(s => s.UserId).HasDatabaseName("ix_login_sessions_user_id");
        // Limpeza de sessões vencidas (job a criar) e índice de expiração pedido na spec 10.2.
        builder.HasIndex(s => s.AbsoluteExpiresAt).HasDatabaseName("ix_login_sessions_absolute_expires_at");
    }
}
