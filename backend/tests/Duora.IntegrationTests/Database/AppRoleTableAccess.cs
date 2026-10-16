namespace Duora.IntegrationTests.Database;

/// <summary>O que o papel da aplicação (<c>duora_app</c>) pode fazer numa tabela (docs/data/model.md, spec 10.7).</summary>
internal enum AppTableAccess
{
    /// <summary><c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c> e <c>DELETE</c>: o padrão dos default privileges.</summary>
    ReadWrite,

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
            // identity (dono: Identity)
            ["identity.consent_records"] = AppTableAccess.ReadWrite,
            ["identity.login_sessions"] = AppTableAccess.ReadWrite,
            ["identity.role_claims"] = AppTableAccess.ReadWrite,
            ["identity.roles"] = AppTableAccess.ReadWrite,
            ["identity.user_claims"] = AppTableAccess.ReadWrite,
            ["identity.user_logins"] = AppTableAccess.ReadWrite,
            ["identity.user_roles"] = AppTableAccess.ReadWrite,
            ["identity.user_tokens"] = AppTableAccess.ReadWrite,
            ["identity.users"] = AppTableAccess.ReadWrite,

            // media (dono: Media)
            ["media.media_assets"] = AppTableAccess.ReadWrite,
            ["media.media_variants"] = AppTableAccess.ReadWrite,
            ["media.uploads"] = AppTableAccess.ReadWrite,

            // ops (dono: Operations; outbox de propriedade comum, modules.md nota 4)
            ["ops.__ef_migrations_history"] = AppTableAccess.None,
            ["ops.audit_events"] = AppTableAccess.AppendOnly,
            ["ops.outbox_deliveries"] = AppTableAccess.ReadWrite,
            ["ops.outbox_events"] = AppTableAccess.ReadWrite,

            // profiles (dono: Profiles)
            ["profiles.profiles"] = AppTableAccess.ReadWrite,

            // safety (dono: Safety)
            ["safety.blocks"] = AppTableAccess.ReadWrite,
            ["safety.user_guards"] = AppTableAccess.ReadWrite,
        };

    /// <summary>Privilégios de tabela do PostgreSQL 18, na grafia de <c>has_table_privilege</c>.</summary>
    public static readonly IReadOnlyList<string> AllPrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"];

    public static IReadOnlyList<string> PrivilegesOf(AppTableAccess access) => access switch
    {
        AppTableAccess.ReadWrite => ["SELECT", "INSERT", "UPDATE", "DELETE"],
        AppTableAccess.AppendOnly => ["SELECT", "INSERT"],
        AppTableAccess.None => [],
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Acesso de tabela desconhecido."),
    };
}
