using Duora.Core.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Duora.Infrastructure.Identity;

public sealed record OwnTimeZone(string? TimeZone, long Version);

/// <summary>Private presentation preference. It does not declare languages or affect matching/session versions.</summary>
public sealed class TimeZoneStore(NpgsqlDataSource source)
{
    public async Task<OwnTimeZone?> GetOwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return (await ReadAsync(connection, userId, false, cancellationToken))?.Value;
    }

    public async Task<OwnTimeZone?> UpdateOwnAsync(Guid userId, long expectedVersion, string? timeZone, CancellationToken cancellationToken)
    {
        if (timeZone is not null && !TimeZones.IsSupported(timeZone))
        {
            throw new ArgumentException("Unsupported IANA time zone.", nameof(timeZone));
        }
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var timeout = new NpgsqlCommand("set local lock_timeout = '2s'", connection);
            await timeout.ExecuteNonQueryAsync(cancellationToken);
            var current = await ReadAsync(connection, userId, true, cancellationToken);
            if (current is null)
            {
                return null;
            }
            if (current.Value.Status is not ("Active" or "PendingEligibility"))
            {
                throw new TimeZonePreferenceRestrictedException();
            }
            if (current.Value.Value.Version != expectedVersion)
            {
                return null;
            }
            await using var command = new NpgsqlCommand("""
                update identity.users set preferred_time_zone = @zone, time_zone_version = time_zone_version + 1
                where id = @user_id
                """, connection);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("zone", NpgsqlDbType.Text, (object?)timeZone ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OwnTimeZone(timeZone, current.Value.Value.Version + 1);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            throw new AccountBusyException(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<(OwnTimeZone Value, string Status)?> ReadAsync(
        NpgsqlConnection connection, Guid userId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select preferred_time_zone, time_zone_version, status from identity.users where id = @user_id" + (forUpdate ? " for update" : ""), connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (new OwnTimeZone(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1)), reader.GetString(2)) : null;
    }
}

public sealed class TimeZonePreferenceRestrictedException : Exception;
