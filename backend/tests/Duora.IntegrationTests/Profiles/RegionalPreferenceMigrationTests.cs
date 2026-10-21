using Duora.Core.Profiles;
using Duora.Infrastructure.Profiles;
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
    public async Task ModeOnlyChangeAndReparentingCannotLeaveInconsistentPreferences()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var owner = await RoleSeparationTests.InsertUserAsync(connection);
        var other = await RoleSeparationTests.InsertUserAsync(connection);
        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await using var create = new NpgsqlCommand("""
                insert into profiles.regional_preferences values(@owner,'countries',1,now()),(@other,'unrestricted',1,now());
                insert into profiles.regional_preference_countries values(@owner,'BR');
                """, connection);
            create.Parameters.AddWithValue("owner", owner);
            create.Parameters.AddWithValue("other", other);
            await create.ExecuteNonQueryAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        foreach (var sql in new[]
        {
            "update profiles.regional_preferences set mode='unrestricted' where user_id=@owner",
            "update profiles.regional_preferences set mode='countries' where user_id=@other; update profiles.regional_preference_countries set user_id=@other where user_id=@owner",
        })
        {
            await using var tx = await connection.BeginTransactionAsync(Ct);
            await using var change = new NpgsqlCommand(sql, connection);
            change.Parameters.AddWithValue("owner", owner);
            change.Parameters.AddWithValue("other", other);
            await change.ExecuteNonQueryAsync(Ct);
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync(Ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task DirectChildWritesSerializeWithTheParentEvenWithoutTheApiGuard()
    {
        await using var source = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var holder = await source.OpenConnectionAsync(Ct);
        var user = await RoleSeparationTests.InsertUserAsync(holder);
        Assert.True(RegionalPreference.TryCreate("countries", ["BR"], out var preference));
        Assert.NotNull(await new RegionalPreferenceStore(source, TimeProvider.System).UpdateOwnAsync(user, 0, preference!, Ct));
        await using var tx = await holder.BeginTransactionAsync(Ct);
        await using (var parent = new NpgsqlCommand("update profiles.regional_preferences set version=2 where user_id=@user", holder))
        {
            parent.Parameters.AddWithValue("user", user);
            await parent.ExecuteNonQueryAsync(Ct);
        }
        await using var child = await source.OpenConnectionAsync(Ct);
        await using var mutate = new NpgsqlCommand("update profiles.regional_preference_countries set country_code=country_code where user_id=@user", child);
        mutate.Parameters.AddWithValue("user", user);
        var pending = mutate.ExecuteNonQueryAsync(Ct);
        await using var observer = await source.OpenConnectionAsync(Ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (true)
        {
            await using var waiting = new NpgsqlCommand("select count(*) from pg_stat_activity where @pid=any(pg_blocking_pids(pid))", observer);
            waiting.Parameters.AddWithValue("pid", holder.ProcessID);
            if ((long)(await waiting.ExecuteScalarAsync(Ct))! > 0)
            { break; }
            Assert.True(DateTimeOffset.UtcNow < deadline, "Direct child writer did not acquire the parent lock.");
            await Task.Yield();
        }
        await tx.CommitAsync(Ct);
        Assert.Equal(1, await pending);
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
        var noProfile = await RoleSeparationTests.InsertUserAsync(connection);
        var noLocation = await RoleSeparationTests.InsertUserAsync(connection);
        await using (var emptyProfile = new NpgsqlCommand("insert into profiles.profiles(user_id,version,updated_at) values(@user,4,'2026-10-01T00:00:00Z')", connection))
        {
            emptyProfile.Parameters.AddWithValue("user", noLocation);
            await emptyProfile.ExecuteNonQueryAsync(Ct);
        }
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
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString);
        foreach (var legacy in new[] { user, noProfile, noLocation })
        {
            var regional = await new RegionalPreferenceStore(source, TimeProvider.System).GetOwnAsync(legacy, Ct);
            Assert.Equal(RegionalPreferenceMode.Pending, regional.Preference.Mode);
            Assert.Equal(0L, regional.Version);
        }
        var languages = await new PreferenceStore(source, TimeProvider.System).GetOwnAsync(noLocation, Ct);
        Assert.True(languages.DeclarationRequired);
        Assert.Empty(languages.CommunicationLanguages);
    }
}
