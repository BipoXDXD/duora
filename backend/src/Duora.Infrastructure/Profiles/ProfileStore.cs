using Duora.Core.Profiles;
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
    private const string Columns = "display_name, bio, version";

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

        await using var command = expectedVersion == OwnProfile.Unsaved.Version
            ? _dataSource.CreateCommand(
                $"""
                insert into profiles.profiles (user_id, display_name, bio, version, updated_at)
                values (@user_id, @display_name, @bio, 1, @now)
                on conflict (user_id) do nothing
                returning {Columns}
                """)
            : _dataSource.CreateCommand(
                $"""
                update profiles.profiles
                set display_name = case when @set_display_name then @display_name else display_name end,
                    bio = case when @set_bio then @bio else bio end,
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
        command.Parameters.Add(TextParameter("display_name", edit.DisplayName?.Value));
        command.Parameters.Add(TextParameter("bio", edit.Bio?.NewBio?.Value));
        return await ReadSingleAsync(command, cancellationToken);
    }

    private static NpgsqlParameter TextParameter(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static async Task<OwnProfile?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OwnProfile(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2));
    }
}
