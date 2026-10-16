using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Sessão ativa como aparece para o titular (API-A09).</summary>
public sealed record ActiveSession(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string Description);

/// <summary>Dados de uma sessão nova; o ticket já vem protegido.</summary>
public sealed record NewLoginSession(
    Guid Id,
    Guid UserId,
    string Key,
    byte[] ProtectedTicket,
    string SecurityStamp,
    string Description,
    DateTimeOffset CreatedAt,
    SessionWindow Window,
    SessionPolicy Policy,
    DateTimeOffset? ReauthenticatedAt,
    DateTimeOffset? MfaVerifiedAt,
    DateTimeOffset? TotpVerifiedAt);

/// <summary>
/// Sessão válida depois do toque: o ticket e o estado que muda sem novo ticket (reautenticação, prova de MFA) ou
/// fora da sessão (papéis da conta, lidos agora, não no login).
/// </summary>
public sealed record TouchedSession(
    byte[] ProtectedTicket,
    SessionPolicy Policy,
    DateTimeOffset? ReauthenticatedAt,
    DateTimeOffset? MfaVerifiedAt,
    DateTimeOffset? TotpVerifiedAt,
    DateTimeOffset AbsoluteExpiresAt,
    bool AccountIsPrivileged);

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

    internal LoginSessions(NpgsqlDataSource dataSource, TimeProvider time)
    {
        _dataSource = dataSource;
        _time = time;
    }

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

    public async Task CreateAsync(NewLoginSession session, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            insert into identity.login_sessions
                (id, user_id, key_hash, ticket, security_stamp, description,
                 created_at, last_seen_at, idle_expires_at, absolute_expires_at,
                 policy, reauthenticated_at, mfa_verified_at, totp_verified_at)
            values
                (@id, @user_id, @key_hash, @ticket, @security_stamp, @description,
                 @created_at, @created_at, @idle_expires_at, @absolute_expires_at,
                 @policy, @reauthenticated_at, @mfa_verified_at, @totp_verified_at)
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
        command.Parameters.AddWithValue("policy", session.Policy.ToString());
        command.Parameters.AddWithValue("reauthenticated_at", (object?)session.ReauthenticatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("mfa_verified_at", (object?)session.MfaVerifiedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("totp_verified_at", (object?)session.TotpVerifiedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Sessão ainda válida, já com a expiração ociosa da sua política renovada; null se não vale mais. Os papéis são
    /// lidos agora: tirar o papel de alguém vale na requisição seguinte, sem esperar o fim da sessão.
    /// </summary>
    public async Task<TouchedSession?> TouchAsync(string key, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            update identity.login_sessions s
            set last_seen_at = @now,
                idle_expires_at = least(
                    @now + case s.policy when 'Administrative' then @administrative_idle else @normal_idle end,
                    s.absolute_expires_at)
            from identity.users u
            where s.key_hash = @key_hash
              and s.revoked_at is null
              and s.idle_expires_at > @now
              and s.absolute_expires_at > @now
              and u.id = s.user_id
              and u.security_stamp = s.security_stamp
            returning s.ticket, s.policy, s.reauthenticated_at, s.mfa_verified_at, s.totp_verified_at,
                s.absolute_expires_at,
                exists (
                    select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                    where ur.user_id = s.user_id and r.name = any(@privileged_roles))
            """);
        command.Parameters.AddWithValue("key_hash", HashKey(key));
        command.Parameters.AddWithValue("now", _time.GetUtcNow());
        command.Parameters.AddWithValue("normal_idle", SessionLifetime.For(SessionPolicy.Normal).Idle);
        command.Parameters.AddWithValue("administrative_idle", SessionLifetime.For(SessionPolicy.Administrative).Idle);
        command.Parameters.AddWithValue("privileged_roles", AccountRoles.Privileged.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TouchedSession(
            reader.GetFieldValue<byte[]>(0),
            Enum.Parse<SessionPolicy>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetBoolean(6));
    }

    public async Task ReplaceTicketAsync(string key, byte[] protectedTicket, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "update identity.login_sessions set ticket = @ticket where key_hash = @key_hash and revoked_at is null");
        command.Parameters.AddWithValue("key_hash", HashKey(key));
        command.Parameters.AddWithValue("ticket", protectedTicket);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeByKeyAsync(string key, string reason, CancellationToken cancellationToken)
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
