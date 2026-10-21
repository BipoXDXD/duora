using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionalPreferenceMigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("countries", "", PostgresErrorCodes.CheckViolation)]
    [InlineData("unrestricted", "insert into profiles.regional_preference_countries values (@user_id,'BR');", PostgresErrorCodes.CheckViolation)]
    [InlineData("pending", "", PostgresErrorCodes.CheckViolation)]
    [InlineData("countries", "insert into profiles.regional_preference_countries values (@user_id,'US');", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("countries", "insert into profiles.regional_preference_countries values (@user_id,'BR'),(@user_id,'BR');", PostgresErrorCodes.UniqueViolation)]
    public async Task DatabaseRejectsInvalidConfirmedPreferences(string mode, string children, string state)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand($"""
            insert into profiles.regional_preferences values (@user_id,@mode,1,now());
            {children}
            """, connection);
        command.Parameters.AddWithValue("user_id", user);
        command.Parameters.AddWithValue("mode", mode);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await command.ExecuteNonQueryAsync(Ct);
            await tx.CommitAsync(Ct);
        });
        Assert.Equal(state, error.SqlState);
    }

    [Fact]
    public async Task DeferredChecksAllowAtomicTransitionsButRejectRemovingTheLastCountry()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await using var insert = new NpgsqlCommand("""
                insert into profiles.regional_preferences values (@user_id,'countries',1,now());
                insert into profiles.regional_preference_countries values (@user_id,'BR');
                """, connection);
            insert.Parameters.AddWithValue("user_id", user);
            await insert.ExecuteNonQueryAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await using var delete = new NpgsqlCommand("delete from profiles.regional_preference_countries where user_id=@user_id", connection);
            delete.Parameters.AddWithValue("user_id", user);
            await delete.ExecuteNonQueryAsync(Ct);
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync(Ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await using var update = new NpgsqlCommand("""
                update profiles.regional_preferences set mode='unrestricted',version=2 where user_id=@user_id;
                delete from profiles.regional_preference_countries where user_id=@user_id;
                """, connection);
            update.Parameters.AddWithValue("user_id", user);
            await update.ExecuteNonQueryAsync(Ct);
            await tx.CommitAsync(Ct);
        }
    }

    [Fact]
    public async Task MigrationDoesNotInferConsentOrChangeExistingProfileAndLanguages()
    {
        var db = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(db.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261021150000_OperationalRetention", cancellationToken: Ct);
        }
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        var user = await RoleSeparationTests.InsertUserAsync(connection);
        await using var command = new NpgsqlCommand("""
            insert into profiles.profiles(user_id,region_code,version,updated_at) values(@user_id,'BR-SP',7,'2026-10-01T00:00:00Z');
            insert into profiles.preferences values(@user_id,true,3,'2026-10-01T00:00:00Z');
            insert into profiles.communication_languages values(@user_id,'pt');
            """, connection);
        command.Parameters.AddWithValue("user_id", user);
        await command.ExecuteNonQueryAsync(Ct);
        await using (var after = PostgresFixture.CreateDbContext(db.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(Ct);
        }
        command.CommandText = """
            select p.country_code || '|' || p.subdivision_code || '|' || p.version || '|' || l.version || '|' || c.language_code,
              p.updated_at='2026-10-01T00:00:00Z'::timestamptz and l.updated_at=p.updated_at,
              (select count(*) from profiles.regional_preferences),
              (select string_agg(code,',' order by code) from profiles.countries)
            from profiles.profiles p join profiles.preferences l using(user_id)
            join profiles.communication_languages c using(user_id) where p.user_id=@user_id
            """;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("BR|BR-SP|7|3|pt", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal("BR", reader.GetString(3));
    }
}
