using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests;

public sealed class MigrationsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task BaselineIsRecordedInOpsMigrationHistory()
    {
        // Como migrador: o app não tem privilégio algum no histórico (reconcile-privileges.sql).
        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // As colunas da tabela de histórico são do EF ("MigrationId"); renomeá-las exigiria API interna do provider.
        await using var command = new NpgsqlCommand(
            """select count(*) from ops.__ef_migrations_history where "MigrationId" like '%\_Baseline'""",
            connection);

        var appliedBaselines = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1L, appliedBaselines);
    }

    [Fact]
    public async Task ModuleTablesExist()
    {
        var tables = await QueryNamesAsync(
            """
            select table_schema || '.' || table_name from information_schema.tables
            where table_schema in ('events', 'experiences', 'identity', 'ops', 'profiles', 'safety') and table_name <> '__ef_migrations_history'
            """);

        Assert.Equal(
            [
                "events.events", "events.pairing_participants", "events.pairings", "events.registrations", "events.rounds",
                "experiences.audio_assets", "experiences.experience_versions", "experiences.game_actions", "experiences.game_participants", "experiences.game_sessions",
                "identity.age_verifications", "identity.consent_records", "identity.login_sessions", "identity.role_claims", "identity.roles", "identity.selfie_verifications",
                "identity.user_claims", "identity.user_logins", "identity.user_roles", "identity.user_tokens",
                "identity.users", "ops.audit_events", "ops.country_database_approvals", "ops.country_database_deliveries", "ops.country_database_incidents", "ops.country_database_notifications", "ops.country_database_retired", "ops.country_database_state", "ops.country_database_versions", "ops.jobs", "ops.outbox_deliveries", "ops.outbox_events", "ops.webhook_receipts",
                "profiles.communication_languages", "profiles.countries", "profiles.preferences", "profiles.profiles", "profiles.regional_preference_countries", "profiles.regional_preferences", "profiles.subdivisions",
                "safety.blocks", "safety.photo_review_appeal_commands", "safety.photo_review_appeals",
                "safety.photo_review_assignments", "safety.photo_review_cases", "safety.photo_review_commands",
                "safety.photo_review_decision_commands", "safety.photo_review_decision_variants", "safety.photo_review_decisions",
                "safety.photo_review_evidence_accesses", "safety.photo_review_grants", "safety.user_guards",
            ],
            tables.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Nomes físicos em snake_case (spec 10.1), inclusive nas tabelas do Identity, cujo mapeamento padrão é PascalCase.
    /// A tabela de histórico do EF fica de fora: as colunas dela são do provider.
    /// </summary>
    [Fact]
    public async Task PhysicalNamesAreSnakeCase()
    {
        var names = await QueryNamesAsync(
            """
            select table_name from information_schema.tables
             where table_schema in ('events', 'experiences', 'identity', 'ops', 'profiles', 'safety') and table_name <> '__ef_migrations_history'
            union all
            select column_name from information_schema.columns
             where table_schema in ('events', 'experiences', 'identity', 'ops', 'profiles', 'safety') and table_name <> '__ef_migrations_history'
            union all
            select constraint_name from information_schema.table_constraints
             where table_schema in ('events', 'experiences', 'identity', 'ops', 'profiles', 'safety') and table_name <> '__ef_migrations_history'
               and constraint_type <> 'CHECK'
            union all
            select indexname from pg_indexes
             where schemaname in ('events', 'experiences', 'identity', 'ops', 'profiles', 'safety') and tablename <> '__ef_migrations_history'
            """);

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]*$"));
    }

    [Fact]
    public async Task ModelHasNoChangesMissingAMigration()
    {
        await using var dbContext = postgres.CreateDbContext();

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task WebDoesNotMigrateOnStartup()
    {
        var emptyDatabase = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);
        // Como migrador, com poder de DDL: se a web tentasse migrar no startup, conseguiria e o teste pegaria.
        await using (var factory = new DuoraWebFactory(emptyDatabase.MigratorConnectionString))
        {
            using var client = factory.CreateClient();
            var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await using var connection = new NpgsqlConnection(emptyDatabase.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select to_regclass('ops.__ef_migrations_history')::text", connection);

        var historyTable = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DBNull.Value, historyTable);
    }

    /// <summary>Como migrador, dono das tabelas: o information_schema esconde o que o usuário conectado não acessa.</summary>
    private async Task<List<string>> QueryNamesAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
