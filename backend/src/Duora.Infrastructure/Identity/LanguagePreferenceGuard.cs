using Npgsql;

namespace Duora.Infrastructure.Identity;

internal static class LanguagePreferenceGuard
{
    public static async Task<T?> RunAsync<T>(NpgsqlDataSource source, Guid userId, Func<NpgsqlConnection, Task<T?>> action, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var guard = new NpgsqlCommand("""
                set local lock_timeout = '2s';
                insert into safety.user_guards (user_id) values (@user_id) on conflict do nothing;
                select 1 from safety.user_guards where user_id = @user_id for update;
                """, connection);
            guard.Parameters.AddWithValue("user_id", userId);
            await guard.ExecuteNonQueryAsync(cancellationToken);
            // Revalidate account status only after acquiring the guard, just like the declaration version.
            await using var status = new NpgsqlCommand("select status from identity.users where id = @user_id", connection);
            status.Parameters.AddWithValue("user_id", userId);
            if (await status.ExecuteScalarAsync(cancellationToken) is not ("Active" or "PendingEligibility"))
            {
                throw new LanguagePreferenceRestrictedException();
            }

            var result = await action(connection);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            throw new AccountBusyException(TimeSpan.FromSeconds(2));
        }
    }
}

public sealed class LanguagePreferenceRestrictedException : Exception;
