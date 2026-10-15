using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests;

public sealed class MigrationsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task BaselineIsRecordedInOpsMigrationHistory()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // As colunas da tabela de histórico são do EF ("MigrationId"); renomeá-las exigiria API interna do provider.
        await using var command = new NpgsqlCommand(
            """select count(*) from ops.__ef_migrations_history where "MigrationId" like '%\_Baseline'""",
            connection);

        var appliedBaselines = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1L, appliedBaselines);
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
        await using (var factory = new DuoraWebFactory(emptyDatabase))
        {
            using var client = factory.CreateClient();
            var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await using var connection = new NpgsqlConnection(emptyDatabase);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select to_regclass('ops.__ef_migrations_history')::text", connection);

        var historyTable = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DBNull.Value, historyTable);
    }
}
