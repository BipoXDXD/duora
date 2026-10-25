using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests.Database;

/// <summary>Real PostgreSQL ACL effects, including inherited grants; no role or data from production.</summary>
public sealed class PhotoReviewReconciliationTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> ProtectedTables = new Dictionary<string, string>
    {
        ["safety.photo_review_grants"] = "id",
        ["safety.photo_review_assignments"] = "id",
        ["safety.photo_review_commands"] = "actor_id",
        ["safety.photo_review_decision_commands"] = "actor_id",
        ["safety.photo_review_decision_variants"] = "decision_id",
        ["safety.photo_review_decisions"] = "id",
        ["safety.photo_review_evidence_accesses"] = "id",
        ["media.review_promotion_variants"] = "generation",
    };
    private static readonly string[] ForbiddenPrivileges = ["UPDATE", "DELETE", "TRUNCATE"];
    private static readonly string[] InheritedGrantTargets =
        ["safety.photo_review_grants", "safety.photo_review_evidence_accesses"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReconciliationRepairsAllEightHistoriesWithoutChangingRowsSchemaOrLegitimateAppend()
    {
        var database = await CreateMigratedAsync();
        await using var owner = new NpgsqlConnection(database.MigratorConnectionString);
        await owner.OpenAsync(Ct);
        var user = await RoleSeparationTests.InsertUserAsync(owner);
        await InsertGrantAsync(owner, user, 1, enabled: true);
        await InsertGrantAsync(owner, user, 2, enabled: false);
        var history = await HistoryAsync(owner);
        var schema = await SchemaAsync(owner);
        var grants = await GrantsAsync(owner, user);
        foreach (var table in ProtectedTables.Keys)
        {
            await ExecuteAsync(owner, $"grant update, delete, truncate on table {table} to duora_app");
            foreach (var privilege in ForbiddenPrivileges)
            {
                Assert.True(await HasPrivilegeAsync(owner, table, privilege));
            }
        }

        await postgres.ReconcilePrivilegesAsync(database.Name, Ct);

        Assert.Equal(history, await HistoryAsync(owner));
        Assert.Equal(schema, await SchemaAsync(owner));
        Assert.Equal(grants, await GrantsAsync(owner, user));
        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync(Ct);
        foreach (var (table, key) in ProtectedTables)
        {
            Assert.True(await HasPrivilegeAsync(owner, table, "SELECT"));
            Assert.True(await HasPrivilegeAsync(owner, table, "INSERT"));
            foreach (var privilege in ForbiddenPrivileges)
            {
                Assert.False(await HasPrivilegeAsync(owner, table, privilege));
            }
            // A compiled allowlist, never participant input. WHERE false exercises ACL without touching rows.
            foreach (var sql in new[] { $"update {table} set {key}={key} where false",
                         $"delete from {table} where false", $"truncate {table}" })
            {
                await using var transaction = await app.BeginTransactionAsync(Ct);
                await using var command = new NpgsqlCommand(sql, app, transaction);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                await transaction.RollbackAsync(Ct);
            }
        }
        await InsertGrantAsync(app, user, 3, enabled: true);
        Assert.Equal("1:true,2:false,3:true", await GrantsAsync(owner, user));
    }

    [Theory]
    [InlineData("safety.photo_review_grants", "UPDATE")]
    [InlineData("safety.photo_review_grants", "DELETE")]
    [InlineData("safety.photo_review_grants", "TRUNCATE")]
    [InlineData("safety.photo_review_evidence_accesses", "UPDATE")]
    [InlineData("safety.photo_review_evidence_accesses", "DELETE")]
    [InlineData("safety.photo_review_evidence_accesses", "TRUNCATE")]
    public async Task PublicPrivilegesRefuseEitherBlockAndRollBackAllReconciliationRevokes(string table, string privilege)
    {
        Assert.Contains(table, ProtectedTables.Keys);
        Assert.Contains(privilege, ForbiddenPrivileges);
        var database = await CreateMigratedAsync();
        await using var owner = new NpgsqlConnection(database.MigratorConnectionString);
        await owner.OpenAsync(Ct);
        var history = await HistoryAsync(owner);
        await ExecuteAsync(owner, "grant update on safety.photo_review_grants to duora_app");
        await ExecuteAsync(owner, $"grant {privilege} on table {table} to public");
        try
        {
            Assert.True(await HasPrivilegeAsync(owner, table, privilege));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                postgres.ReconcilePrivilegesAsync(database.Name, Ct));
            Assert.Contains("duora_app may not rewrite photo review", error.Message, StringComparison.Ordinal);
            Assert.Equal(history, await HistoryAsync(owner));
            Assert.True(await HasPrivilegeAsync(owner, "safety.photo_review_grants", "UPDATE"),
                "The single transaction must roll back earlier direct revokes when any inherited grant remains.");
        }
        finally
        {
            await ExecuteAsync(owner, $"revoke {privilege} on table {table} from public", CancellationToken.None);
        }
        await postgres.ReconcilePrivilegesAsync(database.Name, Ct);
        Assert.False(await HasPrivilegeAsync(owner, "safety.photo_review_grants", "UPDATE"));
        Assert.False(await HasPrivilegeAsync(owner, table, privilege));
    }

    [Fact]
    public async Task RoleInheritedPrivilegesRefuseEitherBlockWithoutChangingSharedFixtureMembership()
    {
        // pg_auth_members is cluster-wide: a private server avoids racing the shared role-inventory tests.
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        var database = await CreateMigratedAsync(isolated);
        await using var admin = new NpgsqlConnection(isolated.ScratchSuperuserConnectionString(database));
        await admin.OpenAsync(Ct);
        foreach (var table in InheritedGrantTargets)
        {
            foreach (var privilege in ForbiddenPrivileges)
            {
                var role = "review_acl_" + Guid.NewGuid().ToString("N");
                await ExecuteAsync(admin, $"create role {role} nologin");
                try
                {
                    await ExecuteAsync(admin, $"grant {privilege} on table {table} to {role}; grant {role} to duora_app");
                    await ExecuteAsync(admin, "grant update on safety.photo_review_grants to duora_app");
                    var history = await HistoryAsync(admin);
                    Assert.True(await HasPrivilegeAsync(admin, table, privilege));
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        isolated.ReconcilePrivilegesAsync(database.Name, Ct));
                    Assert.Contains("duora_app may not rewrite photo review", error.Message, StringComparison.Ordinal);
                    Assert.Equal(history, await HistoryAsync(admin));
                    Assert.True(await HasPrivilegeAsync(admin, "safety.photo_review_grants", "UPDATE"));
                    await using var membership = new NpgsqlCommand("select pg_has_role('duora_app', $1, 'MEMBER')", admin);
                    membership.Parameters.AddWithValue(role);
                    Assert.Equal(true, await membership.ExecuteScalarAsync(Ct));
                }
                finally
                {
                    await ExecuteAsync(admin, $"revoke {role} from duora_app; drop owned by {role}; drop role {role}", CancellationToken.None);
                }
                await isolated.ReconcilePrivilegesAsync(database.Name, Ct);
                Assert.False(await HasPrivilegeAsync(admin, table, privilege));
                Assert.False(await HasPrivilegeAsync(admin, "safety.photo_review_grants", "UPDATE"));
            }
        }
    }

    [Fact]
    public async Task CanonicalPayloadBeforeReviewMigrationsDoesNotCreateSchemaOrFabricateMigrationHistory()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using var owner = new NpgsqlConnection(database.MigratorConnectionString);
        await owner.OpenAsync(Ct);
        await postgres.ReconcilePrivilegesAsync(database.Name, Ct);
        foreach (var table in ProtectedTables.Keys.Append("ops.__ef_migrations_history"))
        {
            await using var absent = new NpgsqlCommand("select to_regclass($1) is null", owner);
            absent.Parameters.AddWithValue(table);
            Assert.Equal(true, await absent.ExecuteScalarAsync(Ct));
        }
        await using (var db = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await db.Database.MigrateAsync(Ct);
        }
        await postgres.ReconcilePrivilegesAsync(database.Name, Ct);
        foreach (var table in ProtectedTables.Keys)
        {
            Assert.False(await HasPrivilegeAsync(owner, table, "UPDATE"));
            Assert.True(await HasPrivilegeAsync(owner, table, "INSERT"));
        }
    }

    private async Task<ScratchDatabase> CreateMigratedAsync(PostgresFixture? fixture = null)
    {
        var server = fixture ?? postgres;
        var database = await server.CreateEmptyDatabaseAsync(Ct);
        await using var db = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
        await db.Database.MigrateAsync(Ct);
        await server.ReconcilePrivilegesAsync(database.Name, Ct);
        return database;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken? cancellationToken = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken ?? Ct);
    }

    private static async Task<bool> HasPrivilegeAsync(NpgsqlConnection connection, string table, string privilege)
    {
        await using var command = new NpgsqlCommand("select has_table_privilege('duora_app', $1, $2)", connection);
        command.Parameters.AddWithValue(table);
        command.Parameters.AddWithValue(privilege);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> HistoryAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select string_agg(\"MigrationId\" || ':' || \"ProductVersion\", ',' order by \"MigrationId\") from ops.__ef_migrations_history", connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> SchemaAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            with targets as (select unnest($1::text[])::regclass as oid), definitions as (
              select 'column:' || a.attrelid || ':' || json_build_array(a.attnum,a.attname,a.atttypid,
                a.atttypmod,a.attnotnull,a.attidentity,a.attgenerated,pg_get_expr(d.adbin,d.adrelid))::text as definition
              from pg_attribute a join targets t on t.oid=a.attrelid
              left join pg_attrdef d on d.adrelid=a.attrelid and d.adnum=a.attnum
              where a.attnum>0 and not a.attisdropped
              union all
              select 'constraint:' || c.conrelid || ':' || c.conname || ':' || pg_get_constraintdef(c.oid)
              from pg_constraint c join targets t on t.oid=c.conrelid
              union all
              select 'index:' || i.indrelid || ':' || pg_get_indexdef(i.indexrelid)
              from pg_index i join targets t on t.oid=i.indrelid)
            select string_agg(definition,E'\n' order by definition) from definitions
            """, connection);
        command.Parameters.AddWithValue(ProtectedTables.Keys.ToArray());
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task InsertGrantAsync(NpgsqlConnection connection, Guid user, long version, bool enabled)
    {
        await using var command = new NpgsqlCommand("insert into safety.photo_review_grants(id,user_id,capability,version,enabled,occurred_at) values($1,$2,'evidence',$3,$4,now())", connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(user);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(enabled);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> GrantsAsync(NpgsqlConnection connection, Guid user)
    {
        await using var command = new NpgsqlCommand("select string_agg(version::text || ':' || enabled::text, ',' order by version) from safety.photo_review_grants where user_id=$1", connection);
        command.Parameters.AddWithValue(user);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
