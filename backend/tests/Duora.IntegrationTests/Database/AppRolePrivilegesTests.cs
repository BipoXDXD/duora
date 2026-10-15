using Npgsql;

namespace Duora.IntegrationTests.Database;

/// <summary>
/// O banco dos testes é criado como o de produção: papéis e default privileges do initdb da imagem
/// (<c>infra/images/postgres</c>), migrations como <c>duora_migrator</c>, <c>reconcile-privileges.sql</c> depois, e a
/// aplicação conectada como <c>duora_app</c>. Aqui se confere o resultado tabela a tabela (spec 10.7, model.md).
/// </summary>
public sealed class AppRolePrivilegesTests(PostgresFixture postgres)
{
    private const string AppRole = "duora_app";

    // Schemas do próprio PostgreSQL; todo o resto é do Duora e entra na enumeração.
    private const string ApplicationSchemaFilter =
        "n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg\\_%'";

    [Fact]
    public async Task EveryTableHasAnEntryInTheAllowlist()
    {
        var tables = await QueryStringsAsync(
            $"""
            select n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p', 'v', 'm', 'f') and {ApplicationSchemaFilter}
            """);

        var missing = tables.Except(AppRoleTableAccess.Expected.Keys).Order(StringComparer.Ordinal).ToList();
        var stale = AppRoleTableAccess.Expected.Keys.Except(tables).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && stale.Count == 0,
            $"Atualize {nameof(AppRoleTableAccess)}.{nameof(AppRoleTableAccess.Expected)}. " +
            $"Sem entrada: [{string.Join(", ", missing)}]. Entrada sem tabela: [{string.Join(", ", stale)}].");
    }

    [Fact]
    public async Task AppHasExactlyTheAllowlistedPrivilegesOnEachTable()
    {
        var granted = await QueryStringsAsync(
            $"""
            select n.nspname || '.' || c.relname || ':' || p.privilege
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            cross join unnest($1::text[]) as p(privilege)
            where c.relkind in ('r', 'p', 'v', 'm', 'f') and {ApplicationSchemaFilter}
              and has_table_privilege('{AppRole}', c.oid, p.privilege)
            """,
            AppRoleTableAccess.AllPrivileges.ToArray());

        var expected = AppRoleTableAccess.Expected
            .SelectMany(entry => AppRoleTableAccess.PrivilegesOf(entry.Value).Select(privilege => $"{entry.Key}:{privilege}"))
            .ToList();
        var missing = expected.Except(granted).Order(StringComparer.Ordinal).ToList();
        var extra = granted.Except(expected).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"Privilégios de {AppRole} fora da allowlist. Faltando: [{string.Join(", ", missing)}]. " +
            $"Sobrando: [{string.Join(", ", extra)}].");
    }

    [Fact]
    public async Task AppHasNoColumnLevelGrants()
    {
        // Privilégio de coluna escaparia da conferência por tabela: has_table_privilege só olha a tabela inteira.
        var grants = await QueryStringsAsync(
            $"""
            select table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
            from information_schema.column_privileges c
            where grantee = '{AppRole}'
              and not exists (select 1 from information_schema.table_privileges t
                              where t.grantee = c.grantee and t.table_schema = c.table_schema
                                and t.table_name = c.table_name and t.privilege_type = c.privilege_type)
            """);

        Assert.Empty(grants);
    }

    [Fact]
    public async Task AppSequencesAllowNextvalButNotSetval()
    {
        var sequences = await QueryStringsAsync(
            $"""
            select n.nspname || '.' || c.relname || ':' ||
                   has_sequence_privilege('{AppRole}', c.oid, 'USAGE')::text || ':' ||
                   has_sequence_privilege('{AppRole}', c.oid, 'UPDATE')::text
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'S' and {ApplicationSchemaFilter}
            """);

        Assert.NotEmpty(sequences);
        Assert.All(sequences, sequence => Assert.EndsWith(":true:false", sequence, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppSeesEveryApplicationSchemaButCannotCreateInAny()
    {
        var schemas = await QueryStringsAsync(
            $"""
            select n.nspname || ':' || has_schema_privilege('{AppRole}', n.oid, 'USAGE')::text || ':' ||
                   has_schema_privilege('{AppRole}', n.oid, 'CREATE')::text
            from pg_namespace n where {ApplicationSchemaFilter} and n.nspname <> 'public'
            """);
        var publicSchema = await QueryStringsAsync(
            $"select has_schema_privilege('{AppRole}', 'public', 'CREATE')::text");

        Assert.Equal(
            AppRoleTableAccess.Expected.Keys.Select(table => table.Split('.')[0]).Distinct().Order(StringComparer.Ordinal)
                .Select(schema => $"{schema}:true:false"),
            schemas.Order(StringComparer.Ordinal));
        Assert.Equal(["false"], publicSchema);
    }

    [Fact]
    public async Task AppIsAnUnprivilegedRoleThatOwnsNothing()
    {
        var role = await QueryStringsAsync(
            $"""
            select concat_ws(':', rolsuper::text, rolcreatedb::text, rolcreaterole::text, rolreplication::text,
                   rolbypassrls::text, rolinherit::text,
                   (select count(*) from pg_auth_members where member = r.oid),
                   has_database_privilege(r.oid, current_database(), 'CREATE')::text,
                   has_database_privilege(r.oid, current_database(), 'TEMPORARY')::text,
                   has_database_privilege(r.oid, current_database(), 'CONNECT')::text,
                   (select count(*) from pg_class where relowner = r.oid),
                   (select count(*) from pg_namespace where nspowner = r.oid),
                   (select count(*) from pg_proc where proowner = r.oid))
            from pg_roles r where rolname = '{AppRole}'
            """);

        // Sem atributos de admin e sem papel herdado; só CONNECT no banco; dono de nada.
        Assert.Equal(["false:false:false:false:false:true:0:false:false:true:0:0:0"], role);
    }

    [Theory]
    [InlineData("create table identity.intruder (id int)")]
    [InlineData("create table ops.intruder (id int)")]
    [InlineData("create table public.intruder (id int)")]
    [InlineData("create schema intruder")]
    [InlineData("create temp table intruder (id int)")]
    [InlineData("alter table identity.users add column intruder int")]
    [InlineData("drop table ops.outbox_events")]
    [InlineData("truncate identity.users")]
    [InlineData("truncate ops.outbox_events")]
    [InlineData("select count(*) from ops.__ef_migrations_history")]
    [InlineData("delete from ops.__ef_migrations_history")]
    public async Task AppStatementIsDenied(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // Transação desfeita sempre: se a proteção regredir, o banco compartilhado da suíte não perde nada.
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // 42501 insufficient_privilege; 42501 também é o código de "must be owner of table".
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task ScratchDatabasesGetTheSameDatabaseAclAsTheMainOne()
    {
        var scratch = await postgres.CreateEmptyDatabaseAsync(TestContext.Current.CancellationToken);
        var scratchName = scratch.Name;

        var acls = await QueryStringsAsync(
            "select coalesce(datacl::text, '') || ':' || pg_get_userbyid(datdba) from pg_database " +
            "where datname = any($1) order by datname = 'duora' desc",
            [PostgresFixture.DatabaseName, scratchName]);

        Assert.Equal(2, acls.Count);
        Assert.Equal(acls[0], acls[1]);
    }

    /// <summary>Como migrador, dono das tabelas; <paramref name="parameter"/> vira o <c>$1</c>, quando houver.</summary>
    private async Task<List<string>> QueryStringsAsync(string sql, string[]? parameter = null)
    {
        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
