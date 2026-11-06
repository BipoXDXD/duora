using Npgsql;

namespace Duora.IntegrationTests.Database;

/// <summary>
/// Os bancos avulsos migrados nascem de uma cópia, não de uma migração. Aqui se confere que a cópia não muda o que um
/// teste enxerga: <c>CREATE DATABASE ... TEMPLATE</c> não copia ACL, dono nem <c>ALTER DATABASE ... SET</c>.
/// </summary>
public sealed class ScratchDatabaseTemplateTests(PostgresFixture postgres)
{
    private const int ConcurrentClones = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigratedScratchDatabasesGetTheSameAclOwnerAndSettingsAsTheMainOne()
    {
        await using var scratch = await postgres.CreateMigratedScratchDatabaseAsync(Ct);

        Assert.Equal(await DatabaseIdentityAsync(PostgresFixture.DatabaseName), await DatabaseIdentityAsync(scratch.Name));
    }

    [Fact]
    public async Task MigratedScratchDatabasesGiveTheAppTheSameSearchPathAsTheMainOne()
    {
        await using var scratch = await postgres.CreateMigratedScratchDatabaseAsync(Ct);

        Assert.Equal(await AppSearchPathAsync(postgres.ConnectionString), await AppSearchPathAsync(scratch.AppConnectionString));
    }

    [Fact]
    public async Task AMigratedCloneDumpsExactlyLikeARealMigration()
    {
        await using var clone = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var migrated = await postgres.CreateInitializedScratchDatabaseAsync(
            (database, token) => postgres.MigrateAsync(database.Name, token), Ct);

        Assert.Equal(await DumpAsync(migrated.Name), await DumpAsync(clone.Name));
    }

    [Fact]
    public async Task ConcurrentClonesSucceedWhileTheMainDatabaseIsInUseAndStayIndependent()
    {
        await using var mainInUse = new NpgsqlConnection(postgres.ConnectionString);
        await mainInUse.OpenAsync(Ct);

        var clones = await Task.WhenAll(Enumerable.Range(0, ConcurrentClones)
            .Select(_ => postgres.CreateMigratedScratchDatabaseAsync(Ct)));
        try
        {
            await ExecuteAsync(clones[0].MigratorConnectionString, "create schema clone_marker");

            Assert.Equal(true, await SchemaExistsAsync(clones[0], "clone_marker"));
            await using var later = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
            Assert.Equal(false, await SchemaExistsAsync(clones[1], "clone_marker"));
            Assert.Equal(false, await SchemaExistsAsync(later, "clone_marker"));
        }
        finally
        {
            foreach (var clone in clones)
            {
                await clone.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TheMigratedTemplateRefusesEvenTheSuperuser()
    {
        await using var scratch = await postgres.CreateEmptyDatabaseAsync(Ct);
        var superuser = new NpgsqlConnectionStringBuilder(postgres.ScratchSuperuserConnectionString(scratch))
        {
            Database = PostgresFixture.MigratedTemplate,
        };
        await using var connection = new NpgsqlConnection(superuser.ConnectionString);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync(Ct));

        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, refused.SqlState);
    }

    private async Task<string?> DatabaseIdentityAsync(string database)
    {
        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(d.datacl::text, '') || ':' || pg_get_userbyid(d.datdba) || ':' || coalesce((
                select string_agg(s.setrole::text || '=' || array_to_string(s.setconfig, ','), ';' order by s.setrole)
                from pg_db_role_setting s where s.setdatabase = d.oid), '')
            from pg_database d where d.datname = $1
            """, connection);
        command.Parameters.AddWithValue(database);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private static async Task<string?> AppSearchPathAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("show search_path", connection);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    // pg_dump cobre objetos, donos, ACLs, default privileges, sequências e linhas; a ordem é determinística.
    private async Task<string> DumpAsync(string database)
    {
        var dump = await postgres.ExecuteInContainerAsync(["pg_dump", "--username", "postgres", "--dbname", database], Ct);
        Assert.True(dump.ExitCode == 0, dump.Stderr);
        return string.Join('\n', dump.Stdout.Split('\n').Where(line => !IsRandomRestrictKey(line)));
    }

    // O pg_dump sorteia a chave de \restrict e \unrestrict a cada execução.
    private static bool IsRandomRestrictKey(string line) =>
        line.StartsWith("\\restrict ", StringComparison.Ordinal) || line.StartsWith("\\unrestrict ", StringComparison.Ordinal);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> SchemaExistsAsync(ScratchDatabase database, string schema)
    {
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select to_regnamespace($1) is not null", connection);
        command.Parameters.AddWithValue(schema);
        return await command.ExecuteScalarAsync(Ct);
    }
}
