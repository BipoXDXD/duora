namespace Duora.IntegrationTests.Database;

/// <summary>O que o papel da aplicação (<c>duora_app</c>) pode fazer numa tabela (docs/data/model.md, spec 10.7).</summary>
internal enum AppTableAccess
{
    /// <summary><c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c> e <c>DELETE</c>: o padrão dos default privileges.</summary>
    ReadWrite,

    /// <summary><c>SELECT</c>, <c>INSERT</c> e <c>UPDATE</c>: preserva o journal e seu pai contra exclusão pelo app.</summary>
    ReadWriteWithoutDelete,

    /// <summary>Catálogo mantido pelo migrador; a aplicação apenas lê.</summary>
    ReadOnly,

    /// <summary>Só <c>SELECT</c> e <c>INSERT</c>: a migration que cria a tabela revoga <c>UPDATE</c>, <c>DELETE</c> e
    /// <c>TRUNCATE</c> (convenção de tabela append-only, ex.: <c>ops.audit_events</c>).</summary>
    AppendOnly,

    /// <summary>Nenhum privilégio: tabela de controle que o deploy fecha em <c>reconcile-privileges.sql</c>.</summary>
    None,
}

/// <summary>
/// Allowlist de todas as tabelas do banco migrado e do acesso esperado de <c>duora_app</c> a cada uma. Tabela nova
/// sem linha aqui, ou linha de tabela que não existe mais, reprova <see cref="AppRolePrivilegesTests"/>.
/// </summary>
/// <remarks>
/// Uma linha por tabela, em ordem alfabética, agrupada por schema: quem cria tabela na migration acrescenta a linha
/// no mesmo PR, e PRs paralelos de módulos diferentes não editam as mesmas linhas.
/// </remarks>
internal static class AppRoleTableAccess
{
    public static readonly IReadOnlyDictionary<string, AppTableAccess> Expected =
        new Dictionary<string, AppTableAccess>(StringComparer.Ordinal)
        {
            ["events.events"] = AppTableAccess.ReadWrite,
            ["events.registrations"] = AppTableAccess.ReadWrite,
            ["events.rounds"] = AppTableAccess.ReadWrite,
            ["events.pairings"] = AppTableAccess.ReadWrite,
            ["events.pairing_participants"] = AppTableAccess.ReadWrite,
            ["experiences.audio_assets"] = AppTableAccess.ReadWrite,
            ["experiences.experience_versions"] = AppTableAccess.ReadWrite,
            ["experiences.game_sessions"] = AppTableAccess.ReadWrite,
            ["experiences.game_participants"] = AppTableAccess.ReadWrite,
            ["experiences.game_actions"] = AppTableAccess.ReadWrite,
            ["ops.jobs"] = AppTableAccess.ReadWrite,

            // identity (dono: Identity)
            ["identity.age_verifications"] = AppTableAccess.ReadWrite,
            ["identity.consent_records"] = AppTableAccess.ReadWrite,
            ["identity.login_sessions"] = AppTableAccess.ReadWrite,
            ["identity.role_claims"] = AppTableAccess.ReadWrite,
            ["identity.roles"] = AppTableAccess.ReadWrite,
            ["identity.selfie_verifications"] = AppTableAccess.ReadWrite,
            ["identity.user_claims"] = AppTableAccess.ReadWrite,
            ["identity.user_logins"] = AppTableAccess.ReadWrite,
            ["identity.user_roles"] = AppTableAccess.ReadWrite,
            ["identity.user_tokens"] = AppTableAccess.ReadWrite,
            ["identity.users"] = AppTableAccess.ReadWrite,

            // media (dono: Media)
            ["media.media_assets"] = AppTableAccess.ReadWrite,
            ["media.media_variants"] = AppTableAccess.ReadWrite,
            // Intenção durável: criar, tomar/reagendar por lease e remover somente após DELETE confirmado.
            ["media.original_cleanup"] = AppTableAccess.ReadWrite,
            ["media.review_candidate_variants"] = AppTableAccess.ReadWrite,
            ["media.review_candidates"] = AppTableAccess.ReadWrite,
            ["media.review_promotion_variants"] = AppTableAccess.AppendOnly,
            ["media.review_promotions"] = AppTableAccess.ReadWrite,
            ["media.upload_guards"] = AppTableAccess.ReadWrite,
            ["media.uploads"] = AppTableAccess.ReadWrite,

            // ops (dono: Operations; outbox de propriedade comum, modules.md nota 4)
            ["ops.__ef_migrations_history"] = AppTableAccess.None,
            ["ops.audit_events"] = AppTableAccess.AppendOnly,
            ["ops.country_database_approvals"] = AppTableAccess.AppendOnly,
            ["ops.country_database_deliveries"] = AppTableAccess.ReadWriteWithoutDelete,
            ["ops.country_database_retired"] = AppTableAccess.AppendOnly,
            ["ops.country_database_incidents"] = AppTableAccess.ReadWrite,
            ["ops.country_database_notifications"] = AppTableAccess.ReadWriteWithoutDelete,
            ["ops.country_database_versions"] = AppTableAccess.AppendOnly,
            ["ops.country_database_state"] = AppTableAccess.ReadWrite,
            ["ops.outbox_deliveries"] = AppTableAccess.ReadWrite,
            ["ops.outbox_events"] = AppTableAccess.ReadWrite,
            // Não é append-only: o processamento muda o estado na mesma transação e a retenção apaga em lote.
            ["ops.webhook_receipts"] = AppTableAccess.ReadWrite,

            // profiles (dono: Profiles)
            ["profiles.communication_languages"] = AppTableAccess.ReadWrite,
            ["profiles.countries"] = AppTableAccess.ReadOnly,
            ["profiles.preferences"] = AppTableAccess.ReadWrite,
            ["profiles.profiles"] = AppTableAccess.ReadWrite,
            ["profiles.regional_preferences"] = AppTableAccess.ReadWrite,
            ["profiles.regional_preference_countries"] = AppTableAccess.ReadWrite,
            ["profiles.subdivisions"] = AppTableAccess.ReadOnly,

            // safety (dono: Safety)
            ["safety.blocks"] = AppTableAccess.ReadWrite,
            ["safety.photo_review_appeal_commands"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_appeals"] = AppTableAccess.ReadWriteWithoutDelete,
            ["safety.photo_review_assignments"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_cases"] = AppTableAccess.ReadWrite,
            ["safety.photo_review_commands"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_decision_commands"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_decision_variants"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_decisions"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_evidence_accesses"] = AppTableAccess.AppendOnly,
            ["safety.photo_review_grants"] = AppTableAccess.AppendOnly,
            ["safety.user_guards"] = AppTableAccess.ReadWrite,
        };

    /// <summary>Privilégios de tabela do PostgreSQL 18, na grafia de <c>has_table_privilege</c>.</summary>
    public static readonly IReadOnlyList<string> AllPrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"];

    public static IReadOnlyList<string> PrivilegesOf(AppTableAccess access) => access switch
    {
        AppTableAccess.ReadOnly => ["SELECT"],
        AppTableAccess.ReadWrite => ["SELECT", "INSERT", "UPDATE", "DELETE"],
        AppTableAccess.ReadWriteWithoutDelete => ["SELECT", "INSERT", "UPDATE"],
        AppTableAccess.AppendOnly => ["SELECT", "INSERT"],
        AppTableAccess.None => [],
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Acesso de tabela desconhecido."),
    };
}
