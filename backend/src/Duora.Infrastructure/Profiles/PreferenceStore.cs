using Duora.Infrastructure.Identity;
using Npgsql;

namespace Duora.Infrastructure.Profiles;

public sealed record OwnPreferences(string[] CommunicationLanguages, bool AllowDifferentDisplayLocales, long Version)
{
    public bool DeclarationRequired => Version == 0;
}

/// <summary>Private declarations, serialized against matching through the shared user guard.</summary>
public sealed class PreferenceStore(NpgsqlDataSource dataSource, TimeProvider time)
{
    public async Task<OwnPreferences> GetOwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, userId, cancellationToken);
    }

    private static async Task<OwnPreferences> ReadAsync(NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select p.allow_different_display_locales, p.version,
                array(select language_code from profiles.communication_languages where user_id = p.user_id order by language_code)
            from profiles.preferences p where user_id = @user_id
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OwnPreferences(reader.GetFieldValue<string[]>(2), reader.GetBoolean(0), reader.GetInt64(1))
            : new OwnPreferences([], false, 0);
    }

    public async Task<OwnPreferences?> UpdateOwnAsync(Guid userId, long expectedVersion, string[] languages, bool allowDifferentLocales, CancellationToken cancellationToken)
    {
        if (languages.Length is < 1 or > 2 || languages.Any(x => x is not ("en" or "pt")) || languages.Distinct(StringComparer.Ordinal).Count() != languages.Length)
        {
            throw new ArgumentException("Unsupported communication declaration.", nameof(languages));
        }

        return await LanguagePreferenceGuard.RunAsync(dataSource, userId, async connection =>
        {
            var current = await ReadAsync(connection, userId, cancellationToken);
            if (current.Version != expectedVersion)
            {
                return null;
            }

            await using var command = new NpgsqlCommand("""
                insert into profiles.preferences (user_id, allow_different_display_locales, version, updated_at)
                values (@user_id, @allow_locales, 1, @now)
                on conflict (user_id) do update set allow_different_display_locales = excluded.allow_different_display_locales,
                    version = profiles.preferences.version + 1, updated_at = excluded.updated_at;
                delete from profiles.communication_languages where user_id = @user_id;
                insert into profiles.communication_languages (user_id, language_code) select @user_id, unnest(@languages::text[]);
                update safety.user_guards set security_version = security_version + 1 where user_id = @user_id;
                """, connection);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("allow_locales", allowDifferentLocales);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            command.Parameters.AddWithValue("languages", languages);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new OwnPreferences(languages.Order(StringComparer.Ordinal).ToArray(), allowDifferentLocales, current.Version + 1);
        }, cancellationToken);
    }
}
