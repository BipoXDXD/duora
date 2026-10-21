using Npgsql;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Dedicated session, not a business transaction. Serializes publication/maintenance across processes.</summary>
internal sealed class CountryDatabaseFileLock(NpgsqlConnection connection) : IAsyncDisposable
{
    internal static async Task<CountryDatabaseFileLock> AcquireAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            await using var q = new NpgsqlCommand("set lock_timeout='2s'; select pg_advisory_lock(1080033)", connection)
            { CommandTimeout = 3 };
            await q.ExecuteNonQueryAsync(ct);
            return new(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var q = new NpgsqlCommand("select pg_advisory_unlock(1080033)", connection) { CommandTimeout = 3 };
            await q.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
