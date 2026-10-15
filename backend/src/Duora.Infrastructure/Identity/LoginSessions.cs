using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Sessão ativa como aparece para o titular (API-A09).</summary>
public sealed record ActiveSession(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string Description);

/// <summary>Dados de uma sessão nova; o ticket já vem protegido.</summary>
internal sealed record NewLoginSession(
    Guid Id,
    Guid UserId,
    string Key,
    byte[] ProtectedTicket,
    string SecurityStamp,
    string Description,
    DateTimeOffset CreatedAt,
    SessionWindow Window);

/// <summary>
/// Persistência das sessões de login (spec 11.1). A chave opaca do cookie só é guardada como hash. Recuperar uma
/// sessão é um único UPDATE que confere revogação, expiração ociosa e absoluta e o security stamp atual da conta,
/// e ao mesmo tempo desliza a expiração ociosa sem passar do limite absoluto. Os instantes vêm do
/// <see cref="TimeProvider"/> da aplicação.
/// </summary>
public sealed class LoginSessions
{
    private const int MaxListedSessions = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    internal LoginSessions(NpgsqlDataSource dataSource, TimeProvider time, SessionLifetime lifetime)
    {
        _dataSource = dataSource;
        _time = time;
        Lifetime = lifetime;
    }

    internal SessionLifetime Lifetime { get; }

    public async Task<IReadOnlyList<ActiveSession>> ListActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            select id, created_at, last_seen_at, description
            from identity.login_sessions
            where user_id = @user_id
              and revoked_at is null
              and idle_expires_at > @now
              and absolute_expires_at > @now
            order by last_seen_at desc, id
            limit @limit
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("limit", MaxListedSessions);

        var sessions = new List<ActiveSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new ActiveSession(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3)));
        }

        return sessions;
    }

    /// <summary>
    /// Revoga uma sessão do próprio titular. Falso quando a sessão não existe ou é de outra pessoa, casos que a
    /// API não distingue. Revogar de novo uma sessão própria é sucesso.
    /// </summary>
    public async Task<bool> RevokeOwnAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            update identity.login_sessions
            set revoked_at = coalesce(revoked_at, @now),
                revocation_reason = coalesce(revocation_reason, @reason)
            where id = @id and user_id = @user_id
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("reason", "revoked-by-owner");
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal async Task RevokeAllAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            update identity.login_sessions
            set revoked_at = @now, revocation_reason = @reason
            where user_id = @user_id and revoked_at is null
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task CreateAsync(NewLoginSession session, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            insert into identity.login_sessions
                (id, user_id, key_hash, ticket, security_stamp, description,
                 created_at, last_seen_at, idle_expires_at, absolute_expires_at)
            values
                (@id, @user_id, @key_hash, @ticket, @security_stamp, @description,
                 @created_at, @created_at, @idle_expires_at, @absolute_expires_at)
            """);
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("user_id", session.UserId);
        command.Parameters.AddWithValue("key_hash", HashKey(session.Key));
        command.Parameters.AddWithValue("ticket", session.ProtectedTicket);
        command.Parameters.AddWithValue("security_stamp", session.SecurityStamp);
        command.Parameters.AddWithValue("description", session.Description);
        command.Parameters.AddWithValue("created_at", session.CreatedAt);
        command.Parameters.AddWithValue("idle_expires_at", session.Window.IdleExpiresAt);
        command.Parameters.AddWithValue("absolute_expires_at", session.Window.AbsoluteExpiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Ticket protegido da sessão ainda válida, já com a expiração ociosa renovada; null se não vale mais.</summary>
    internal async Task<byte[]?> TouchAsync(string key, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            update identity.login_sessions s
            set last_seen_at = @now,
                idle_expires_at = least(@idle_until, s.absolute_expires_at)
            from identity.users u
            where s.key_hash = @key_hash
              and s.revoked_at is null
              and s.idle_expires_at > @now
              and s.absolute_expires_at > @now
              and u.id = s.user_id
              and u.security_stamp = s.security_stamp
            returning s.ticket
            """);
        var now = _time.GetUtcNow();
        command.Parameters.AddWithValue("key_hash", HashKey(key));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("idle_until", now + Lifetime.Idle);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    internal async Task ReplaceTicketAsync(string key, byte[] protectedTicket, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "update identity.login_sessions set ticket = @ticket where key_hash = @key_hash and revoked_at is null");
        command.Parameters.AddWithValue("key_hash", HashKey(key));
        command.Parameters.AddWithValue("ticket", protectedTicket);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task RevokeByKeyAsync(string key, string reason, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            update identity.login_sessions
            set revoked_at = coalesce(revoked_at, @now),
                revocation_reason = coalesce(revocation_reason, @reason)
            where key_hash = @key_hash
            """);
        command.Parameters.AddWithValue("key_hash", HashKey(key));
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
