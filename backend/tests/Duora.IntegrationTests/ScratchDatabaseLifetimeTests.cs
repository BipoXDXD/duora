using Npgsql;

namespace Duora.IntegrationTests;

public sealed class ScratchDatabaseLifetimeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyTheRegisteredHandleCanDropItsDatabaseAndOtherDatabasePoolsRemainUsable()
    {
        await using var first = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using var other = await postgres.CreateEmptyDatabaseAsync(Ct);
        var otherPool = new NpgsqlConnectionStringBuilder(other.AppConnectionString)
        {
            ApplicationName = "owned-scratch-foreign-pool",
            ConnectionIdleLifetime = 300,
        }.ConnectionString;
        PostgresFixture.TrackScratchPool(otherPool);
        int processId;
        await using (var connection = new NpgsqlConnection(otherPool))
        {
            await connection.OpenAsync(Ct);
            processId = connection.ProcessID;
        }

        foreach (var forged in new[] { first with { }, first with { Name = other.Name }, first with { Name = PostgresFixture.DatabaseName } })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => forged.DisposeAsync().AsTask());
        }
        Assert.True(await ExistsAsync(first.Name));
        Assert.True(await ExistsAsync(other.Name));
        Assert.True(await ExistsAsync(PostgresFixture.DatabaseName));

        await first.DisposeAsync();

        Assert.False(await ExistsAsync(first.Name));
        Assert.True(await ExistsAsync(other.Name));
        await using var stillOwnedElsewhere = new NpgsqlConnection(otherPool);
        await stillOwnedElsewhere.OpenAsync(Ct);
        Assert.Equal(processId, stillOwnedElsewhere.ProcessID);
    }

    [Fact]
    public async Task OwnedCleanupRefusesAnActiveConsumerAndClosesOnlyItsTrackedIdlePoolsBeforeRetry()
    {
        await using var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        var labeled = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        {
            ApplicationName = "owned-scratch-custom-pool",
            ConnectionIdleLifetime = 300,
        }.ConnectionString;
        PostgresFixture.TrackScratchPool(labeled);
        await using (var idle = new NpgsqlConnection(labeled))
        {
            await idle.OpenAsync(Ct);
        }
        await using (var active = new NpgsqlConnection(database.AppConnectionString))
        {
            await active.OpenAsync(Ct);

            var refused = await Assert.ThrowsAsync<PostgresException>(() => database.DisposeAsync().AsTask());

            Assert.Equal(PostgresErrorCodes.ObjectInUse, refused.SqlState);
            Assert.True(await ExistsAsync(database.Name));
            await using var proof = new NpgsqlCommand("select 1", active);
            Assert.Equal(1, await proof.ExecuteScalarAsync(Ct));
        }

        await database.DisposeAsync();

        Assert.False(await ExistsAsync(database.Name));
        await database.DisposeAsync();
        Assert.True(await ExistsAsync(PostgresFixture.DatabaseName));
    }

    [Fact]
    public async Task FailedScratchInitializationRemovesOnlyTheDatabaseCreatedForThatAttempt()
    {
        await using var other = await postgres.CreateEmptyDatabaseAsync(Ct);
        string? created = null;

        await Assert.ThrowsAsync<IOException>(() => postgres.CreateInitializedScratchDatabaseAsync((database, _) =>
        {
            created = database.Name;
            throw new IOException("Synthetic scratch initialization failure.");
        }, Ct));

        Assert.NotNull(created);
        Assert.False(await ExistsAsync(created));
        Assert.True(await ExistsAsync(other.Name));
        Assert.True(await ExistsAsync(PostgresFixture.DatabaseName));
    }

    private async Task<bool> ExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select exists(select 1 from pg_database where datname=$1)", connection);
        command.Parameters.AddWithValue(name);
        return await command.ExecuteScalarAsync(Ct) is true;
    }
}
