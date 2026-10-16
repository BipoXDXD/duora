using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// D13: a migration ParticipantRoleBackfill dá o papel de participante à conta sem papel, cadastrada por uma
/// instância ainda na versão anterior durante o deploy, e não mexe em conta que já tem papel.
/// </summary>
public sealed class ParticipantRoleBackfillTests(PostgresFixture postgres)
{
    private const string MigrationBeforeBackfill = "20261016022454_AdminMfa";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccountsWithoutARoleBecomeParticipantsAndPrivilegedOnesStayAsTheyAre()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync(MigrationBeforeBackfill, cancellationToken: Ct);
        }

        Guid withoutRole;
        Guid moderator;
        await using (var connection = new NpgsqlConnection(database.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            withoutRole = await RoleSeparationTests.InsertUserAsync(connection);
            moderator = await RoleSeparationTests.InsertUserAsync(connection);
            await RoleSeparationTests.GrantAsync(connection, moderator, Duora.Core.Identity.AccountRoles.Moderator);
        }

        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(Ct);
        }

        Assert.Equal(["participant"], await RolesOfAsync(database.AppConnectionString, withoutRole));
        Assert.Equal(["moderator"], await RolesOfAsync(database.AppConnectionString, moderator));
    }

    private static async Task<List<string>> RolesOfAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select r.name from identity.user_roles ur join identity.roles r on r.id = ur.role_id
            where ur.user_id = $1 order by r.name
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }
}
