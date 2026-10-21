using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Npgsql;

namespace Duora.Infrastructure.Profiles;

public sealed record OwnRegionalPreferences(RegionalPreference Preference, long Version);

/// <summary>Private regional choice, with its own version and the shared matching/account guard.</summary>
public sealed class RegionalPreferenceStore(NpgsqlDataSource dataSource, TimeProvider time)
{
    public async Task<OwnRegionalPreferences> GetOwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, userId, cancellationToken);
    }

    private static async Task<OwnRegionalPreferences> ReadAsync(NpgsqlConnection connection, Guid userId, CancellationToken ct)
    {
        // One statement, hence one MVCC snapshot for mode, version and countries.
        await using var command = new NpgsqlCommand("""
            select p.mode, p.version,
                array(select country_code from profiles.regional_preference_countries where user_id=p.user_id order by country_code)
            from profiles.regional_preferences p where user_id=@user_id
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new(RegionalPreference.Pending, 0);
        }
        if (!RegionalPreference.TryCreate(reader.GetString(0), reader.GetFieldValue<string[]>(2), out var preference))
        {
            throw new InvalidOperationException("Invalid stored regional preference.");
        }
        return new(preference!, reader.GetInt64(1));
    }

    public Task<OwnRegionalPreferences?> UpdateOwnAsync(Guid userId, long expectedVersion, RegionalPreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (preference.Mode == RegionalPreferenceMode.Pending)
        {
            throw new ArgumentException("An explicit regional preference is required.", nameof(preference));
        }
        return LanguagePreferenceGuard.RunAsync(dataSource, userId, async connection =>
        {
            var current = await ReadAsync(connection, userId, cancellationToken);
            if (current.Version != expectedVersion)
            {
                return null;
            }
            await using var command = new NpgsqlCommand("""
                insert into profiles.regional_preferences(user_id,mode,version,updated_at)
                values(@user_id,@mode,1,@now)
                on conflict(user_id) do update set mode=excluded.mode,
                    version=profiles.regional_preferences.version+1, updated_at=excluded.updated_at;
                delete from profiles.regional_preference_countries where user_id=@user_id;
                insert into profiles.regional_preference_countries(user_id,country_code)
                    select @user_id, unnest(@countries::text[]);
                update safety.user_guards set security_version=security_version+1 where user_id=@user_id;
                """, connection);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("mode", preference.Mode == RegionalPreferenceMode.Countries ? "countries" : "unrestricted");
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            command.Parameters.AddWithValue("countries", preference.CountryCodes.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new OwnRegionalPreferences(preference, current.Version + 1);
        }, cancellationToken);
    }
}
