using Npgsql;

namespace Duora.Infrastructure.Identity;

public sealed record OwnLocale(string Locale, long Version);

public sealed class LocaleStore(NpgsqlDataSource dataSource)
{
    public async Task<OwnLocale?> GetOwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, userId, cancellationToken);
    }

    private static async Task<OwnLocale?> ReadAsync(NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select coalesce(preferred_locale, 'en-US'), locale_version from identity.users where id = @user_id", connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new OwnLocale(reader.GetString(0), reader.GetInt64(1)) : null;
    }

    public async Task<OwnLocale?> UpdateOwnAsync(Guid userId, long expectedVersion, string locale, CancellationToken cancellationToken)
    {
        if (locale is not ("en-US" or "pt-BR"))
        {
            throw new ArgumentException("Unsupported locale.", nameof(locale));
        }

        return await LanguagePreferenceGuard.RunAsync(dataSource, userId, async connection =>
        {
            var current = await ReadAsync(connection, userId, cancellationToken);
            if (current?.Version != expectedVersion)
            {
                return null;
            }

            await using var command = new NpgsqlCommand("""
                update identity.users set preferred_locale = @locale, locale_version = locale_version + 1 where id = @user_id;
                update safety.user_guards set security_version = security_version + 1 where user_id = @user_id;
                """, connection);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("locale", locale);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new OwnLocale(locale, current.Version + 1);
        }, cancellationToken);
    }
}
