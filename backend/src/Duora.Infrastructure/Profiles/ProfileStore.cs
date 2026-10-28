using Duora.Core.Profiles;
using Duora.Infrastructure.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Duora.Infrastructure.Profiles;

/// <summary>
/// Leitura e gravação do perfil do próprio titular. Toda consulta filtra pelo <c>user_id</c> da sessão; não há
/// operação que receba o id de outra pessoa. A gravação é condicional à versão lida (spec 3.4 e 8.1): se outra aba
/// ou outro dispositivo gravou antes, nada muda e o chamador recebe <c>null</c> para responder 412.
/// </summary>
public sealed class ProfileStore
{
    private const string Columns = "display_name, bio, version, country_code, subdivision_code";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    internal ProfileStore(NpgsqlDataSource dataSource, TimeProvider time)
    {
        _dataSource = dataSource;
        _time = time;
    }

    /// <summary>Perfil do titular; quem ainda não salvou recebe o perfil vazio da versão 0, sem criar linha.</summary>
    public async Task<OwnProfile> GetOwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            $"select {Columns} from profiles.profiles where user_id = @user_id");
        command.Parameters.AddWithValue("user_id", userId);
        return await ReadSingleAsync(command, cancellationToken) ?? OwnProfile.Unsaved;
    }

    /// <summary>
    /// Aplica a edição se a versão atual ainda for <paramref name="expectedVersion"/>; devolve o perfil gravado, ou
    /// <c>null</c> quando a versão não confere. Duas gravações concorrentes com a mesma versão: uma vence, a outra
    /// recebe <c>null</c> (o UPDATE reavalia a condição depois do lock; o INSERT da versão 0 cai no ON CONFLICT).
    /// </summary>
    public async Task<OwnProfile?> UpdateOwnAsync(
        Guid userId,
        long expectedVersion,
        ProfileEdit edit,
        CancellationToken cancellationToken)
    {
        if (edit.ChangesNothing)
        {
            var current = await GetOwnAsync(userId, cancellationToken);
            return current.Version == expectedVersion ? current : null;
        }

        if (edit.Location is not null || edit.Region is not null)
        {
            return await LanguagePreferenceGuard.RunAsync(_dataSource, userId, async connection =>
            {
                var saved = await WriteAsync(userId, expectedVersion, edit, connection, cancellationToken);
                if (saved is not null)
                {
                    await using var marker = new NpgsqlCommand(
                        "update safety.user_guards set security_version=security_version+1 where user_id=@user_id", connection);
                    marker.Parameters.AddWithValue("user_id", userId);
                    await marker.ExecuteNonQueryAsync(cancellationToken);
                }
                return saved;
            }, cancellationToken);
        }
        return await WriteAsync(userId, expectedVersion, edit, null, cancellationToken);
    }

    private async Task<OwnProfile?> WriteAsync(Guid userId, long expectedVersion, ProfileEdit edit,
        NpgsqlConnection? connection, CancellationToken cancellationToken)
    {
        var location = edit.Location ?? (edit.Region is null ? null
            : new ProfileLocation(edit.Region.NewRegion is null ? null : "BR", edit.Region.NewRegion?.Value));
        await using var command = expectedVersion == OwnProfile.Unsaved.Version
            ? CreateCommand(connection,
                $"""
                insert into profiles.profiles (user_id, display_name, bio, country_code, subdivision_code, version, updated_at)
                values (@user_id, @display_name, @bio, @country_code, @subdivision_code, 1, @now)
                on conflict (user_id) do nothing
                returning {Columns}
                """)
            : CreateCommand(connection,
                $"""
                update profiles.profiles
                set display_name = case when @set_display_name then @display_name else display_name end,
                    bio = case when @set_bio then @bio else bio end,
                    country_code = case when @set_location then @country_code else country_code end,
                    subdivision_code = case when @set_location then @subdivision_code else subdivision_code end,
                    version = version + 1,
                    updated_at = @now
                where user_id = @user_id and version = @expected_version
                returning {Columns}
                """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("expected_version", expectedVersion);
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("set_display_name", edit.DisplayName is not null);
        command.Parameters.AddWithValue("set_bio", edit.Bio is not null);
        command.Parameters.AddWithValue("set_location", location is not null);
        command.Parameters.Add(TextParameter("display_name", edit.DisplayName?.Value));
        command.Parameters.Add(TextParameter("bio", edit.Bio?.NewBio?.Value));
        command.Parameters.Add(TextParameter("country_code", location?.CountryCode));
        command.Parameters.Add(TextParameter("subdivision_code", location?.SubdivisionCode));
        return await ReadSingleAsync(command, cancellationToken);
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection? connection, string sql) =>
        connection is null ? _dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, connection);

    private static NpgsqlParameter TextParameter(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static async Task<OwnProfile?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var country = reader.IsDBNull(3) ? null : reader.GetString(3);
        var subdivision = reader.IsDBNull(4) ? null : reader.GetString(4);
        var legacyRegion = country == "BR" && subdivision is not null && RegionCode.TryParse(subdivision, out _)
            ? subdivision
            : null;
        return new OwnProfile(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            legacyRegion,
            reader.GetInt64(2),
            country,
            subdivision);
    }
}
