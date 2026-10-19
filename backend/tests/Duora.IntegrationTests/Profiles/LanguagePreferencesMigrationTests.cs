using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Profiles;
using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class LanguagePreferencesMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task LegacyAccountMigratesWithoutImplicitProficiency()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261016102312_AdminBootstrap", cancellationToken: ct);
        }
        Guid userId;
        await using (var connection = new NpgsqlConnection(database.AppConnectionString))
        {
            await connection.OpenAsync(ct);
            userId = await RoleSeparationTests.InsertUserAsync(connection);
        }
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(ct);
        }
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var preferences = await new PreferenceStore(source, TimeProvider.System).GetOwnAsync(userId, ct);
        var locale = await new LocaleStore(source).GetOwnAsync(userId, ct);
        Assert.True(preferences.DeclarationRequired);
        Assert.Empty(preferences.CommunicationLanguages);
        Assert.False(preferences.AllowDifferentDisplayLocales);
        Assert.Equal(new OwnLocale("en-US", 0), locale);
    }
}
